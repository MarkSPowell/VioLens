using Violens.ProxyCore.Http;

namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Every session captured so far, in memory. <see cref="SessionAdded"/> is
/// the change feed a UI subscribes to. Bodies are capped, and
/// <see cref="MaxSessions"/> evicts the oldest sessions; nothing is spilled
/// to disk yet.
/// </summary>
public sealed class SessionStore
{
    private readonly object _gate = new();
    private readonly List<Session> _sessions = [];
    private int _nextId = 1;
    private int _maxSessions;

    /// <summary>
    /// Fiddler Classic's "Keep: N sessions": once there are more than this,
    /// the oldest are removed (raising <see cref="SessionsRemoved"/>). Zero,
    /// the default, keeps every session. Lowering it trims straight away.
    /// </summary>
    public int MaxSessions
    {
        get
        {
            lock (_gate)
            {
                return _maxSessions;
            }
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            List<int> removed;
            lock (_gate)
            {
                _maxSessions = value;
                removed = TrimLocked();
            }

            RaiseRemoved(removed);
        }
    }

    /// <summary>
    /// Raised after a session is recorded, on the thread that recorded it (a
    /// connection thread). UI subscribers must marshal to their own thread.
    /// </summary>
    public event Action<Session>? SessionAdded;

    public Session Add(string host, DateTimeOffset startedAt, CapturedRequest request, CapturedResponse response) =>
        Add(host, startedAt, request, response, flags: null);

    /// <summary>
    /// The HTTPS-session overload, kept separate (not a default parameter) so
    /// extensions compiled against earlier versions still find it.
    /// </summary>
    public Session Add(
        string host,
        DateTimeOffset startedAt,
        CapturedRequest request,
        CapturedResponse response,
        IReadOnlyDictionary<string, string>? flags) =>
        Add(host, startedAt, request, response, flags, "https");

    /// <summary>
    /// For a session that already carries Fiddler-style flags (see
    /// <see cref="Session.Flags"/>); an empty dictionary is stored as null.
    /// </summary>
    public Session Add(
        string host,
        DateTimeOffset startedAt,
        CapturedRequest request,
        CapturedResponse response,
        IReadOnlyDictionary<string, string>? flags,
        string scheme) =>
        Add(host, startedAt, request, response, flags, scheme, timings: null);

    /// <summary>
    /// As above, with the exchange's <see cref="Session.Timings"/>.
    /// </summary>
    public Session Add(
        string host,
        DateTimeOffset startedAt,
        CapturedRequest request,
        CapturedResponse response,
        IReadOnlyDictionary<string, string>? flags,
        string scheme,
        SessionTimings? timings) =>
        Add(host, startedAt, request, response, flags, scheme, timings, webSocket: null);

    /// <summary>
    /// As above, for a request that opened a WebSocket connection: its
    /// messages are added to <paramref name="webSocket"/> as they pass (see
    /// <see cref="Session.WebSocket"/>).
    /// </summary>
    public Session Add(
        string host,
        DateTimeOffset startedAt,
        CapturedRequest request,
        CapturedResponse response,
        IReadOnlyDictionary<string, string>? flags,
        string scheme,
        SessionTimings? timings,
        WebSockets.WebSocketLog? webSocket)
    {
        Session session;
        List<int> evicted;
        lock (_gate)
        {
            session = new Session(
                _nextId++, host, startedAt, request, response,
                Flags: flags is { Count: > 0 } ? flags : null,
                Scheme: scheme)
            {
                Timings = timings,
                WebSocket = webSocket,
            };
            _sessions.Add(session);
            evicted = TrimLocked();
        }

        // Outside the lock, so subscribers can call back in and slow ones don't
        // hold up other connections. Added before removed, so a subscriber
        // never sees a removal of a session it hasn't been told about.
        SessionAdded?.Invoke(session);
        RaiseRemoved(evicted);
        return session;
    }

    /// <summary>Removes the oldest sessions beyond <see cref="MaxSessions"/>; returns their ids.</summary>
    private List<int> TrimLocked()
    {
        var excess = _maxSessions == 0 ? 0 : _sessions.Count - _maxSessions;
        if (excess <= 0)
        {
            return [];
        }

        var removed = _sessions.Take(excess).Select(s => s.Id).ToList();
        _sessions.RemoveRange(0, excess);
        return removed;
    }

    private void RaiseRemoved(List<int> removed)
    {
        if (removed.Count > 0)
        {
            SessionsRemoved?.Invoke(removed);
        }
    }

    /// <summary>
    /// Raised after sessions are removed (<see cref="Remove"/> or
    /// <see cref="Clear"/>), with their ids. Like <see cref="SessionAdded"/>,
    /// on whatever thread removed them.
    /// </summary>
    public event Action<IReadOnlyList<int>>? SessionsRemoved;

    /// <summary>
    /// Removes the sessions with these ids (unknown ids are ignored) and
    /// returns how many were removed. Ids aren't reused: the next session
    /// still gets the next number, as in Fiddler.
    /// </summary>
    public int Remove(IEnumerable<int> ids)
    {
        var wanted = new HashSet<int>(ids);
        List<int> removed;
        lock (_gate)
        {
            removed = _sessions.Where(s => wanted.Contains(s.Id)).Select(s => s.Id).ToList();
            _sessions.RemoveAll(s => wanted.Contains(s.Id));
        }

        if (removed.Count > 0)
        {
            SessionsRemoved?.Invoke(removed);
        }

        return removed.Count;
    }

    /// <summary>Removes every session and returns how many there were.</summary>
    public int Clear()
    {
        List<int> removed;
        lock (_gate)
        {
            removed = _sessions.Select(s => s.Id).ToList();
            _sessions.Clear();
        }

        if (removed.Count > 0)
        {
            SessionsRemoved?.Invoke(removed);
        }

        return removed.Count;
    }

    public IReadOnlyList<Session> Snapshot()
    {
        lock (_gate)
        {
            return _sessions.ToArray();
        }
    }

    /// <summary>
    /// The id the next session will probably get, for scripts and extensions
    /// reading <c>oSession.id</c> before their session is recorded. Not a
    /// reservation: another connection may record a session first.
    /// </summary>
    public int PeekNextId()
    {
        lock (_gate)
        {
            return _nextId;
        }
    }
}
