using System.Collections.Concurrent;
using System.Threading.Channels;
using Piper.Runtime;

namespace Piper.Messaging;

/// <summary>
/// The local transport: queues and topics in memory, for running and testing with no broker. Each subscription handles
/// one message at a time, in order (so messages with one key are too). A message published to a topic with no
/// subscriptions is dropped, as a broker's is. It keeps what was sent and what was dead-lettered, and <see cref="Idle"/>
/// waits until everything sent has been handled, for a test to look at the result.
/// </summary>
public sealed class MemoryTransport : IMessageTransport
{
    private readonly ConcurrentDictionary<string, Channel<Envelope>> _queues = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Channel<Envelope>>> _topics = new();
    private readonly ConcurrentQueue<Envelope> _sent = new();
    private readonly ConcurrentQueue<DeadLetter> _deadLetters = new();
    private int _pending;
    private TaskCompletionSource _idle = NewIdle();

    /// <summary>Registers the in-memory transport as the local provider of <see cref="IMessageTransport"/>, <c>memory</c>.</summary>
    public static void RegisterProviders() =>
        PiperProviders.Register<IMessageTransport>("memory", _ => new MemoryTransport(), local: true);

    /// <summary>Every envelope sent or published, in order.</summary>
    public IReadOnlyList<Envelope> Sent => [.. _sent];

    /// <summary>Every message dead-lettered: rejected by its handler, or failed on its last attempt.</summary>
    public IReadOnlyList<DeadLetter> DeadLetters => [.. _deadLetters];

    public Task SendAsync(Envelope envelope)
    {
        _sent.Enqueue(envelope);
        var targets = envelope.Kind == DestinationKind.Queue
            ? [Queue(envelope.Destination)]
            : _topics.TryGetValue(envelope.Destination, out var subscriptions) ? subscriptions.Values.ToList() : [];
        foreach (var channel in targets)
            Deliver(channel, envelope);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(Subscription subscription, Func<Envelope, CancellationToken, Task<Handled>> handle)
    {
        var channel = subscription.Kind == DestinationKind.Queue
            ? Queue(subscription.Destination)
            : _topics.GetOrAdd(subscription.Destination, _ => new()).GetOrAdd(subscription.Name, _ => Channel.CreateUnbounded<Envelope>());
        var stop = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in channel.Reader.ReadAllAsync(stop.Token))
                {
                    try
                    {
                        switch (await handle(envelope, stop.Token))
                        {
                            case Handled.RetryHandled { After: var after }:
                                Deliver(channel, envelope with { Attempt = envelope.Attempt + 1, NotBefore = DateTimeOffset.UtcNow + after });
                                break;
                            case Handled.RejectHandled { Reason: var reason }:
                                _deadLetters.Enqueue(new DeadLetter(subscription, envelope, reason));
                                break;
                        }
                    }
                    finally
                    {
                        MarkHandled();
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        });
        return Task.FromResult<IAsyncDisposable>(new Running(stop, loop));
    }

    /// <summary>Waits until every message sent, published or retried has been handled (or dead-lettered).</summary>
    public Task Idle()
    {
        lock (_sent)
            return Volatile.Read(ref _pending) == 0 ? Task.CompletedTask : _idle.Task;
    }

    private Channel<Envelope> Queue(string name) => _queues.GetOrAdd(name, _ => Channel.CreateUnbounded<Envelope>());

    /// <summary>Puts an envelope in a subscription's channel, now or when its <c>NotBefore</c> comes.</summary>
    private void Deliver(Channel<Envelope> channel, Envelope envelope)
    {
        lock (_sent)
        {
            if (_pending++ == 0)
                _idle = NewIdle();
        }
        var wait = envelope.NotBefore is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (wait <= TimeSpan.Zero)
            channel.Writer.TryWrite(envelope);
        else
            _ = Task.Delay(wait).ContinueWith(_ => channel.Writer.TryWrite(envelope), TaskScheduler.Default);
    }

    private void MarkHandled()
    {
        lock (_sent)
        {
            if (--_pending == 0)
                _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource NewIdle() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Running(CancellationTokenSource stop, Task loop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await loop;
            stop.Dispose();
        }
    }
}
