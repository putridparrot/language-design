namespace Piper.Messaging;

/// <summary>
/// What became of a message, as its handler says: <see cref="Done"/>; <see cref="RetryLater"/>, to be handled again
/// after a while (what it needs isn't there yet); or <see cref="Reject"/>, to the dead letters, as retrying won't help.
/// A handler that throws is retried, with backoff, and dead-lettered after the bus's last attempt.
/// </summary>
public abstract record Handled
{
    private Handled() { }

    /// <summary>Handled: the message is done with.</summary>
    public static Handled Done { get; } = new DoneHandled();

    /// <summary>To be handled again after <paramref name="after"/>.</summary>
    public static Handled RetryLater(TimeSpan after) => new RetryHandled(after);

    /// <summary>Not to be handled, for <paramref name="reason"/>: it goes to the dead letters, where someone can look at it.</summary>
    public static Handled Reject(string reason) => new RejectHandled(reason);

    public sealed record DoneHandled : Handled
    {
        public override string ToString() => "Done";
    }

    public sealed record RetryHandled(TimeSpan After) : Handled
    {
        public override string ToString() => $"RetryLater({After})";
    }

    public sealed record RejectHandled(string Reason) : Handled
    {
        public override string ToString() => $"Reject({Reason})";
    }
}

/// <summary>
/// <c>command PlaceOrder(…) queue "orders";</c>: a message for one receiver, sent to <paramref name="queue"/>; whichever
/// consumer's instance takes it from there handles it, once.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandAttribute(string queue) : Attribute
{
    public string Queue { get; } = queue;
}

/// <summary>
/// <c>event OrderPlaced(…) topic "order-events" key orderId;</c>: a message for every subscriber, published to
/// <paramref name="topic"/>; each consumer gets its own copy. With a <see cref="Key"/> (a field's name), messages with the
/// same key are handled in the order they were published.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EventAttribute(string topic) : Attribute
{
    public string Topic { get; } = topic;

    public string? Key { get; init; }
}

/// <summary>Whether a destination is a queue (one receiver for each message) or a topic (a copy for each subscription).</summary>
public enum DestinationKind
{
    Queue,
    Topic,
}

/// <summary>
/// A message as transports carry it: where it's going, what it is (its type's name), its body as JSON, its ordering key,
/// when it may be handled, and how many times it has been.
/// </summary>
public sealed record Envelope(
    Guid Id, string Destination, DestinationKind Kind, string Type, string Body, string? Key = null,
    DateTimeOffset? NotBefore = null, int Attempt = 1);

/// <summary>
/// A consumer's subscription: a queue (shared by every instance of a consumer, each message to one of them), or a
/// topic with the consumer's name (a copy of each event for every consumer).
/// </summary>
public sealed record Subscription(string Destination, DestinationKind Kind, string Name);

/// <summary>A message no handler could handle, with why.</summary>
public sealed record DeadLetter(Subscription Subscription, Envelope Envelope, string Reason);

/// <summary>
/// A broker, as the bus uses one: what every provider (the in-memory transport, a broker's adapter) implements, chosen by
/// configuration at startup (<c>"Messaging": { "Provider": "memory" }</c>). It carries envelopes to their destination,
/// delivers each subscription's to its handler, and does what the handler's <see cref="Handled"/> says: done, delivered
/// again after a while (with its attempt counted), or dead-lettered.
/// </summary>
public interface IMessageTransport
{
    /// <summary>Sends an envelope to its queue, or publishes it to its topic's subscriptions, when its <c>NotBefore</c> comes.</summary>
    Task SendAsync(Envelope envelope);

    /// <summary>Delivers <paramref name="subscription"/>'s envelopes to <paramref name="handle"/> until the result is disposed.</summary>
    Task<IAsyncDisposable> SubscribeAsync(Subscription subscription, Func<Envelope, CancellationToken, Task<Handled>> handle);
}
