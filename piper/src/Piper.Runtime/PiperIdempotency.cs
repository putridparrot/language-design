namespace Piper.Runtime;

/// <summary>The response an idempotent request was answered with, kept to answer its repeats.</summary>
public sealed record IdempotentResponse(int Status, string? ContentType, string? Location, byte[] Body);

/// <summary>What became of a claim on an idempotency key.</summary>
public enum IdempotencyState
{
    /// <summary>The key is new (or its last use has expired): this request is the one to run, and holds it.</summary>
    Claimed,

    /// <summary>A request with this key ran: its <see cref="IdempotencyClaim.Response"/> is the answer.</summary>
    Done,

    /// <summary>A request with this key is running now.</summary>
    Busy,

    /// <summary>The key was used for a different request (another body, route or method).</summary>
    Mismatch,
}

public sealed record IdempotencyClaim(IdempotencyState State, IdempotentResponse? Response = null);

/// <summary>
/// Where idempotent requests' keys and responses are kept, so a request repeated with the same <c>Idempotency-Key</c>
/// (a client retrying after a timeout) is answered with the first one's response instead of running again: a
/// database's (Piper.Data's <c>DatabaseIdempotency</c>, shared by every pod of a service), or
/// <see cref="MemoryIdempotencyStore"/>, one process's own.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims <paramref name="key"/> for a request whose content hashes to <paramref name="fingerprint"/>, for
    /// <paramref name="lease"/> (after which a request that never finished, its process gone, no longer holds it).
    /// </summary>
    Task<IdempotencyClaim> ClaimAsync(string key, string fingerprint, TimeSpan lease);

    /// <summary>Keeps the claimed request's response for <paramref name="keep"/>, to answer repeats with.</summary>
    Task CompleteAsync(string key, IdempotentResponse response, TimeSpan keep);

    /// <summary>Lets the key go without a response (the request failed), so a repeat runs again.</summary>
    Task ReleaseAsync(string key);
}

/// <summary>Idempotency keys kept in this process: for tests, and services of one process.</summary>
public sealed class MemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Entry(string Fingerprint, DateTime Until, IdempotentResponse? Response);

    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _gate = new();

    public Task<IdempotencyClaim> ClaimAsync(string key, string fingerprint, TimeSpan lease)
    {
        var now = PiperRuntime.Current.UtcNow;
        lock (_gate)
        {
            // Expired entries go as keys are claimed, so the dictionary doesn't grow without end.
            foreach (var gone in _entries.Where(e => e.Value.Until <= now).Select(e => e.Key).ToList())
                _entries.Remove(gone);
            if (_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult(entry.Fingerprint != fingerprint ? new IdempotencyClaim(IdempotencyState.Mismatch)
                    : entry.Response is { } response ? new IdempotencyClaim(IdempotencyState.Done, response)
                    : new IdempotencyClaim(IdempotencyState.Busy));
            }
            _entries[key] = new Entry(fingerprint, now + lease, null);
            return Task.FromResult(new IdempotencyClaim(IdempotencyState.Claimed));
        }
    }

    public Task CompleteAsync(string key, IdempotentResponse response, TimeSpan keep)
    {
        var now = PiperRuntime.Current.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
                _entries[key] = entry with { Until = now + keep, Response = response };
        }
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Response is null)
                _entries.Remove(key);
        }
        return Task.CompletedTask;
    }
}
