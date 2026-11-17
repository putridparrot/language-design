namespace Piper.Runtime;

/// <summary>
/// An actor's <c>on failure restart 3 per minute;</c>: whether it may restart again, at most <c>limit</c> times in any
/// <c>window</c>; past that, it stops, as Erlang's supervisors give up on a child that keeps failing.
/// </summary>
public sealed class PiperRestarts(int limit, TimeSpan window)
{
    private readonly Queue<DateTime> _restarts = new();

    /// <summary>Counts a restart, if one more is allowed in the window; false if the actor should stop instead.</summary>
    public bool Allow()
    {
        var now = DateTime.UtcNow;
        lock (_restarts)
        {
            while (_restarts.Count > 0 && now - _restarts.Peek() >= window)
                _restarts.Dequeue();
            if (_restarts.Count >= limit)
                return false;
            _restarts.Enqueue(now);
            return true;
        }
    }
}
