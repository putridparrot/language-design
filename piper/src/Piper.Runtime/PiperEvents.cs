using System.Collections.Concurrent;
using System.Text.Json;

namespace Piper.Runtime;

/// <summary>
/// Where a <c>persisted</c> actor's events are kept: one stream for each actor (<c>Product/tea</c>), each event as
/// JSON, in order. An actor replays its stream as it starts, and appends the events of each message it handles
/// together, once its handler has finished.
/// </summary>
public interface IEventStore
{
    /// <summary>The stream's events, oldest first; none for a stream not written yet.</summary>
    Task<IReadOnlyList<string>> LoadAsync(string stream);

    /// <summary>
    /// Adds <paramref name="events"/> to the end of the stream, if it holds <paramref name="expected"/> events now;
    /// otherwise something else wrote to it (the same actor running twice, on two nodes), and this throws
    /// <see cref="EventStreamConflictException"/> having written nothing.
    /// </summary>
    Task AppendAsync(string stream, int expected, IReadOnlyList<string> events);
}

/// <summary>A stream was written to by something else since this actor read it: two actors with one identity.</summary>
public sealed class EventStreamConflictException(string stream, int expected, int actual)
    : Exception($"The events of {stream} were written by something else: expected {expected} of them, found {actual}. " +
                "Is the same actor running twice?");

/// <summary>Events in memory: for tests, and programs whose actors needn't outlive the process.</summary>
public sealed class MemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, List<string>> _streams = new();

    public Task<IReadOnlyList<string>> LoadAsync(string stream)
    {
        var events = _streams.GetOrAdd(stream, _ => []);
        lock (events)
            return Task.FromResult<IReadOnlyList<string>>([.. events]);
    }

    public Task AppendAsync(string stream, int expected, IReadOnlyList<string> events)
    {
        var existing = _streams.GetOrAdd(stream, _ => []);
        lock (existing)
        {
            if (existing.Count != expected)
                throw new EventStreamConflictException(stream, expected, existing.Count);
            existing.AddRange(events);
        }
        return Task.CompletedTask;
    }

    /// <summary>The streams written, for a test to look at.</summary>
    public IReadOnlyList<string> Streams => [.. _streams.Keys.Order(StringComparer.Ordinal)];
}

/// <summary>
/// What a <c>persisted</c> actor's generated code calls: its events as JSON (a Piper enum: <c>{"kind": "Received",
/// "quantity": 5}</c>), and the events of the message being handled, saved together when it's done.
/// </summary>
public sealed class PiperEventLog<TEvent>(IEventStore store, string stream)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<TEvent> _pending = [];

    /// <summary>How many events the stream held when last read or written: what the next append expects.</summary>
    private int _version;

    /// <summary>The stream's events, decoded, to replay; and where the next append starts.</summary>
    public async Task<IReadOnlyList<TEvent>> ReplayAsync()
    {
        var stored = await store.LoadAsync(stream);
        _version = stored.Count;
        _pending.Clear();
        return [.. stored.Select(json => JsonSerializer.Deserialize<TEvent>(json, Json)
            ?? throw new InvalidOperationException($"An event of {stream} is empty."))];
    }

    /// <summary>An event the handler emitted, applied already, to be saved when the handler finishes.</summary>
    public void Add(TEvent @event) => _pending.Add(@event);

    /// <summary>Saves the handled message's events, together; nothing if it emitted none.</summary>
    public async Task CommitAsync()
    {
        if (_pending.Count == 0)
            return;
        var events = _pending.Select(e => JsonSerializer.Serialize(e, Json)).ToList();
        _pending.Clear();
        await store.AppendAsync(stream, _version, events);
        _version += events.Count;
    }

    /// <summary>Drops the handled message's events: its handler threw, so its state is put back too.</summary>
    public void Discard() => _pending.Clear();
}
