using Piper.Runtime;

namespace Piper.Messaging;

/// <summary>
/// Sends and publishes into an outbox (<see cref="Bus.In"/>), rather than to the broker: each message is saved with the
/// change it tells of, in the same transaction, and a relay (<see cref="Bus.Relay"/>) sends it once it's saved. Routed
/// as the bus routes it, so a command still goes to its queue and an event to its topic.
/// </summary>
public sealed class OutboxSender(IOutbox outbox)
{
    /// <summary>Adds a command, for its queue, to the outbox; after <paramref name="delay"/>, if given, once sent.</summary>
    public Task Send<T>(T command, TimeSpan? delay = null) where T : notnull
    {
        outbox.Add(ToOutbox(Bus.Envelope(command, DestinationKind.Queue, delay)));
        return Task.CompletedTask;
    }

    /// <summary>Adds an event, for its topic, to the outbox; after <paramref name="delay"/>, if given, once sent.</summary>
    public Task Publish<T>(T @event, TimeSpan? delay = null) where T : notnull
    {
        outbox.Add(ToOutbox(Bus.Envelope(@event, DestinationKind.Topic, delay)));
        return Task.CompletedTask;
    }

    private static OutboxMessage ToOutbox(Envelope envelope) =>
        new(envelope.Id, envelope.Destination, envelope.Kind.ToString(), envelope.Type, envelope.Body, envelope.Key, envelope.NotBefore,
            PiperRuntime.Current.UtcNow);

    internal static Envelope FromOutbox(OutboxMessage message) =>
        new(message.Id, message.Destination, Enum.Parse<DestinationKind>(message.Kind), message.Type, message.Body, message.Key, message.NotBefore);
}

/// <summary>
/// How a relay works through an outbox: up to <see cref="Batch"/> messages at a time, claimed for <see cref="Lease"/>
/// (long enough to send them), looking again every <see cref="Poll"/> when there was nothing to send.
/// </summary>
public sealed record OutboxOptions
{
    public int Batch { get; init; } = 100;

    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether it sends in the background, looking every <see cref="Poll"/>; or only when asked
    /// (<see cref="OutboxRelay.FlushAsync"/>), as a test may want, to know when it has.</summary>
    public bool Background { get; init; } = true;
}

/// <summary>
/// Sends an outbox's saved messages through the bus's transport, oldest first, marking each batch sent once it's gone,
/// until it's disposed. Several can share an outbox (one in each pod): each claims its batch first. A message sent
/// but not yet marked when a relay stops is sent again by another, once its claim runs out: at least once.
/// </summary>
public sealed class OutboxRelay : IAsyncDisposable
{
    private readonly IOutboxStore _store;
    private readonly IMessageTransport _transport;
    private readonly OutboxOptions _options;
    private readonly string _claimant = $"relay-{PiperRouting.NewGuid():N}";
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _batches;

    internal OutboxRelay(IOutboxStore store, IMessageTransport transport, OutboxOptions options)
    {
        _store = store;
        _transport = transport;
        _options = options;
        _loop = options.Background ? PiperRuntime.Current.Start(RunAsync) : Task.CompletedTask;
    }

    /// <summary>How many messages it has sent.</summary>
    public long Relayed { get; private set; }

    /// <summary>Sends what's waiting now, until nothing is (for a test, rather than waiting for the next poll): how many.</summary>
    public async Task<int> FlushAsync()
    {
        var total = 0;
        while (await RelayBatchAsync() is > 0 and var sent)
            total += sent;
        return total;
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (await RelayBatchAsync() > 0)
                    continue;
            }
            catch (Exception error) when (!_stop.IsCancellationRequested)
            {
                // The database or the broker is unreachable: the messages wait, and are tried again next time.
                Console.Error.WriteLine($"Piper.Messaging: the outbox couldn't be relayed: {error.Message}");
            }
            try
            {
                await PiperRuntime.Current.Delay(_options.Poll, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<int> RelayBatchAsync()
    {
        // A claim of its own for each batch: the background loop and a FlushAsync mustn't take each other's for theirs.
        var claimed = await _store.ClaimAsync($"{_claimant}/{Interlocked.Increment(ref _batches)}", _options.Batch, _options.Lease);
        if (claimed.Count == 0)
            return 0;
        foreach (var message in claimed)
            await _transport.SendAsync(OutboxSender.FromOutbox(message));
        await _store.MarkSentAsync([.. claimed.Select(m => m.Id)]);
        Relayed += claimed.Count;
        return claimed.Count;
    }

    public async ValueTask DisposeAsync()
    {
        // Not CancelAsync, which runs what waits on it on the thread pool: in a simulation, nothing may.
        _stop.Cancel();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}
