using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Piper.Messaging;

/// <summary>How a bus retries a handler that throws: up to <see cref="Attempts"/> in all, waiting <see cref="Backoff"/>,
/// doubled each time, before the next; then the message is dead-lettered.</summary>
public sealed record BusOptions
{
    public int Attempts { get; init; } = 5;

    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Sends commands, publishes events, and hands messages to consumers, over whichever transport the app chose (the
/// in-memory one locally, a broker's in the cloud). A message's destination comes from its declaration (a
/// <c>messages</c> block's <c>command … queue "orders"</c>), so code says what it means, not where it goes; messages are
/// JSON, as Piper writes its types.
/// </summary>
public sealed class Bus(IMessageTransport transport, BusOptions? options = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BusOptions _options = options ?? new BusOptions();

    /// <summary>The transport it uses: the in-memory one, for a test to look at what was sent.</summary>
    public IMessageTransport Transport => transport;

    /// <summary>Sends a command to its queue, for one receiver; after <paramref name="delay"/>, if given.</summary>
    public Task Send<T>(T command, TimeSpan? delay = null) where T : notnull
    {
        var route = Route.Of(typeof(T));
        if (route.Kind != DestinationKind.Queue)
            throw new InvalidOperationException($"{typeof(T).Name} is an event: publish it, rather than send it.");
        return transport.SendAsync(Envelope(command, route, delay));
    }

    /// <summary>Publishes an event to its topic, for every subscriber; after <paramref name="delay"/>, if given.</summary>
    public Task Publish<T>(T @event, TimeSpan? delay = null) where T : notnull
    {
        var route = Route.Of(typeof(T));
        if (route.Kind != DestinationKind.Topic)
            throw new InvalidOperationException($"{typeof(T).Name} is a command: send it, rather than publish it.");
        return transport.SendAsync(Envelope(@event, route, delay));
    }

    /// <summary>A handler for messages of type <typeparamref name="T"/>, which a consumer gives <see cref="Consume"/>.</summary>
    public static MessageHandler Handler<T>(Func<T, Handled> handle) => new(typeof(T), message => Task.FromResult(handle((T)message)));

    /// <summary>An async handler for messages of type <typeparamref name="T"/>.</summary>
    public static MessageHandler HandlerAsync<T>(Func<T, Task<Handled>> handle) => new(typeof(T), message => handle((T)message));

    /// <summary>
    /// Starts <paramref name="consumer"/>'s handlers: a subscription to each queue its commands come to, and to each topic
    /// its events are published on (named for the consumer, so each consumer gets every event). Disposing the result
    /// stops them.
    /// </summary>
    public async Task<IAsyncDisposable> Consume(string consumer, IEnumerable<MessageHandler> handlers)
    {
        var subscriptions = new List<IAsyncDisposable>();
        foreach (var group in handlers.GroupBy(h => Route.Of(h.Type)))
        {
            var byType = group.ToDictionary(h => h.Type.Name);
            var subscription = new Subscription(group.Key.Destination, group.Key.Kind, consumer);
            subscriptions.Add(await transport.SubscribeAsync(subscription, (envelope, _) => Deliver(envelope, byType)));
        }
        return new Subscriptions(subscriptions);
    }

    /// <summary>
    /// A delivered envelope, handled: its handler's <see cref="Handled"/>; if the handler throws, retried after the
    /// backoff, and dead-lettered after the last attempt; and dead-lettered if nothing here handles its type.
    /// </summary>
    private async Task<Handled> Deliver(Envelope envelope, Dictionary<string, MessageHandler> handlers)
    {
        if (!handlers.TryGetValue(envelope.Type, out var handler))
            return Handled.Reject($"Nothing here handles a {envelope.Type}.");
        object message;
        try
        {
            message = JsonSerializer.Deserialize(envelope.Body, handler.Type, Json)
                ?? throw new JsonException($"A {envelope.Type} can't be empty.");
        }
        catch (JsonException e)
        {
            return Handled.Reject($"It isn't a {envelope.Type}: {e.Message}");
        }
        try
        {
            return await handler.Handle(message);
        }
        catch (Exception e)
        {
            return envelope.Attempt >= _options.Attempts
                ? Handled.Reject($"It failed on its last attempt ({envelope.Attempt}): {e.GetType().Name}: {e.Message}")
                : Handled.RetryLater(_options.Backoff * Math.Pow(2, envelope.Attempt - 1));
        }
    }

    private static Envelope Envelope<T>(T message, Route route, TimeSpan? delay) where T : notnull =>
        new(Guid.CreateVersion7(), route.Destination, route.Kind, typeof(T).Name, JsonSerializer.Serialize(message, Json),
            route.Key?.GetValue(message)?.ToString(), delay is { } wait ? DateTimeOffset.UtcNow + wait : null);

    /// <summary>Where a message type goes, from its declaration's attribute: a queue, or a topic and its key's property.</summary>
    private sealed record Route(string Destination, DestinationKind Kind, PropertyInfo? Key)
    {
        private static readonly ConcurrentDictionary<Type, Route> Routes = new();

        public static Route Of(Type type) => Routes.GetOrAdd(type, t =>
            t.GetCustomAttribute<CommandAttribute>() is { } command ? new Route(command.Queue, DestinationKind.Queue, null)
            : t.GetCustomAttribute<EventAttribute>() is { } @event
                ? new Route(@event.Topic, DestinationKind.Topic, @event.Key is { } key ? t.GetProperty(key) : null)
            : throw new InvalidOperationException($"{t.Name} isn't a message: declare it in a messages block, as a command or an event."));
    }

    private sealed class Subscriptions(List<IAsyncDisposable> subscriptions) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var subscription in subscriptions)
                await subscription.DisposeAsync();
        }
    }
}

/// <summary>A consumer's handler for one message type, made by <see cref="Bus.Handler{T}"/>.</summary>
public sealed record MessageHandler(Type Type, Func<object, Task<Handled>> Handle);
