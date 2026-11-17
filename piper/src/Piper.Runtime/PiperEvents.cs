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

    /// <summary>
    /// The stream's events from the <paramref name="from"/>th (counting from 0) on: those after a snapshot. A store
    /// that can read part of a stream does; this one reads it all and skips the start.
    /// </summary>
    async Task<IReadOnlyList<string>> LoadAsync(string stream, int from) => [.. (await LoadAsync(stream)).Skip(from)];

    /// <summary>
    /// The stream's latest snapshot (its actor's state after its first <c>Version</c> events), or null if it has none,
    /// or the store doesn't keep them: then the actor replays all its events, which is slower but the same.
    /// </summary>
    Task<StoredSnapshot?> LoadSnapshotAsync(string stream) => Task.FromResult<StoredSnapshot?>(null);

    /// <summary>
    /// Keeps <paramref name="snapshot"/> as the stream's latest, unless it has a later one. A store that doesn't keep
    /// snapshots ignores it.
    /// </summary>
    Task SaveSnapshotAsync(string stream, StoredSnapshot snapshot) => Task.CompletedTask;
}

/// <summary>An actor's state as JSON, after the first <paramref name="Version"/> events of its stream.</summary>
public sealed record StoredSnapshot(int Version, string State);

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

    private readonly ConcurrentDictionary<string, StoredSnapshot> _snapshots = new();

    public Task<StoredSnapshot?> LoadSnapshotAsync(string stream) => Task.FromResult(_snapshots.GetValueOrDefault(stream));

    public Task SaveSnapshotAsync(string stream, StoredSnapshot snapshot)
    {
        _snapshots.AddOrUpdate(stream, snapshot, (_, kept) => kept.Version >= snapshot.Version ? kept : snapshot);
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

    /// <summary>Where replaying starts: after the snapshot the actor's state was restored from, or 0.</summary>
    private int _from;

    /// <summary>The version of the latest snapshot this actor saved or read: when the next is due.</summary>
    private int _snapshotted;

    /// <summary>
    /// The stream's latest snapshot, for an actor that takes them, before it replays the events after it; one with
    /// <see cref="PiperSnapshot.Found"/> false if there's none. If the actor can't restore it (its state's types have
    /// changed since), it calls <see cref="ForgetSnapshot"/> and replays every event instead.
    /// </summary>
    public async Task<PiperSnapshot> LoadSnapshotAsync()
    {
        var stored = await store.LoadSnapshotAsync(stream);
        _from = _snapshotted = stored?.Version ?? 0;
        return stored is null ? PiperSnapshot.None : PiperSnapshot.Read(stored.State);
    }

    /// <summary>The snapshot couldn't be restored: replay every event, from the first.</summary>
    public void ForgetSnapshot() => _from = 0;

    /// <summary>The stream's events (after the snapshot restored, if any), decoded, to replay; and where the next append starts.</summary>
    public async Task<IReadOnlyList<TEvent>> ReplayAsync()
    {
        var stored = _from == 0 ? await store.LoadAsync(stream) : await store.LoadAsync(stream, _from);
        _version = _from + stored.Count;
        _from = 0;
        _pending.Clear();
        return [.. stored.Select(json => JsonSerializer.Deserialize<TEvent>(json, Json)
            ?? throw new InvalidOperationException($"An event of {stream} is empty."))];
    }

    /// <summary>Whether <paramref name="every"/> events have been saved since the last snapshot, so another is due.</summary>
    public bool SnapshotDue(int every) => _version - _snapshotted >= every;

    /// <summary>
    /// Keeps the actor's state, after the events saved so far, as the stream's latest snapshot. It's only to make
    /// starting quicker (the events are kept already), so a store that fails to keep it doesn't fail the message: the
    /// failure is reported (<see cref="PiperDiagnostics"/>: logged the first time, counted every time), and the next
    /// message tries again.
    /// </summary>
    public async Task SaveSnapshotAsync(PiperSnapshot snapshot)
    {
        try
        {
            await store.SaveSnapshotAsync(stream, new StoredSnapshot(_version, snapshot.Write()));
            _snapshotted = _version;
            if (_snapshotsFailing)
                PiperDiagnostics.SnapshotsRecovered(stream, _version);
            _snapshotsFailing = false;
        }
        catch (Exception e)
        {
            PiperDiagnostics.SnapshotFailed(stream, _version, e, log: !_snapshotsFailing);
            _snapshotsFailing = true;
        }
    }

    /// <summary>Whether the last snapshot failed to be kept: the next failure isn't logged again, only counted.</summary>
    private bool _snapshotsFailing;

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
