namespace Violens.ProxyCore.Sessions;

/// <summary>
/// Collects items that arrive one at a time, from any thread, and hands
/// them on in batches: the first item after a delivery asks
/// <c>schedule</c> for one callback, and everything that arrives before
/// that callback runs goes out with it. Made for the session list, where
/// <see cref="SessionStore.SessionAdded"/> fires once per captured session
/// on a connection's own thread: posting one UI-thread callback for each
/// floods the dispatcher under heavy traffic (a page load is easily
/// hundreds of sessions in a second), while one callback per short interval
/// adds them all at once, at the same cost to the person watching.
///
/// Order is kept, within and across batches. <see cref="Flush"/> delivers
/// whatever's waiting immediately, on the caller's thread, for when
/// something on the receiving side needs everything that's arrived so far
/// to be there first (removing sessions, say: a removal handled before the
/// batch holding those sessions was delivered would let them reappear).
/// The scheduled callback is then a no-op, unless more has arrived since.
/// Delivery is never concurrent with itself as long as <c>schedule</c>'s
/// callbacks and <see cref="Flush"/> all run on one thread, which is what
/// they're for.
/// </summary>
internal sealed class CoalescingQueue<T>
{
    private readonly object _gate = new();
    private readonly Action<Action> _schedule;
    private readonly Action<IReadOnlyList<T>> _deliver;
    private List<T> _waiting = [];
    private bool _scheduled;

    /// <param name="schedule">
    /// Arranges for the callback it's given to run once, later -- on the UI
    /// thread, after a short delay, for the session list.
    /// </param>
    /// <param name="deliver">Receives each batch, in arrival order; never an empty one.</param>
    public CoalescingQueue(Action<Action> schedule, Action<IReadOnlyList<T>> deliver)
    {
        _schedule = schedule;
        _deliver = deliver;
    }

    /// <summary>Queues <paramref name="item"/>, scheduling a delivery if none is pending. Safe from any thread.</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            _waiting.Add(item);
            if (_scheduled)
            {
                return;
            }

            _scheduled = true;
        }

        _schedule(Deliver);
    }

    /// <summary>Delivers everything waiting now, on this thread.</summary>
    public void Flush() => Deliver();

    private void Deliver()
    {
        List<T> batch;
        lock (_gate)
        {
            _scheduled = false;
            if (_waiting.Count == 0)
            {
                return;
            }

            batch = _waiting;
            _waiting = [];
        }

        _deliver(batch);
    }
}
