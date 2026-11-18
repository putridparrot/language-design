using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Piper.Runtime;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Piper.Messaging.RabbitMq;

/// <summary>
/// The messaging plugin's bus over RabbitMQ: the <c>rabbitmq</c> provider of <see cref="IMessageTransport"/>, chosen by
/// configuration (<c>"Messaging": { "Provider": "rabbitmq", "Connection": "amqp://user:password@host:5672/" }</c>).
/// What the bus means, in RabbitMQ's terms:
/// <list type="bullet">
/// <item><b>A command</b> (<c>queue "orders"</c>): a durable queue of that name, sent to through the default exchange;
/// each consumer's instances share it, so each message is handled by one of them.</item>
/// <item><b>An event</b> (<c>topic "order-events"</c>): a durable fanout exchange of that name, and a queue for each
/// consumer bound to it (<c>order-events.Billing</c>), so each consumer gets every event. An event with a <c>key</c> is
/// handled in order: its consumers' queues let one instance at a time take messages (single active consumer), one at a
/// time.</item>
/// <item><b>A delay</b> (a send's or publish's <c>delay</c>, and a handler's <c>RetryLater</c>): a queue whose messages
/// all wait the same time (<c>piper.delay.orders.5000</c>), then dead-letter to where they were going (for a retry, back
/// to the one consumer's queue). The times are steps about a quarter apart, so a message waits at least as long as asked,
/// and at most a quarter longer, and a long wait never holds up a short one. No broker plugin is needed.</item>
/// <item><b>A reject</b> (and a handler's last failed attempt): the consumer's queue's dead letters,
/// <c>order-events.Billing.dead</c>, with the reason in the <c>piper-reason</c> header.</item>
/// </list>
/// Messages are persistent and published with confirms, so a send has reached the broker when it returns; a delivery is
/// acknowledged once its handler has finished (and its retry or dead letter is published), so one that's interrupted is
/// delivered again: at least once. The queues and exchanges are declared as they're first used.
/// </summary>
public sealed class RabbitMqTransport : IMessageTransport, IAsyncDisposable
{
    private const string Attempt = "piper-attempt", Key = "piper-key", NotBefore = "piper-not-before", Reason = "piper-reason";

    private readonly ConnectionFactory _factory;
    private readonly ushort _prefetch;
    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly SemaphoreSlim _publishing = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _declared = new();
    private IConnection? _connection;
    private IChannel? _publisher;

    /// <param name="connection">The broker's URI: <c>amqp://user:password@host:5672/vhost</c>.</param>
    /// <param name="prefetch">How many messages each subscription may be handling or holding at once (1 for an ordered one).</param>
    public RabbitMqTransport(string connection, ushort prefetch = 16)
    {
        _factory = new ConnectionFactory
        {
            Uri = new Uri(connection),
            ClientProvidedName = "piper",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
        };
        _prefetch = prefetch;
    }

    /// <summary>
    /// Registers RabbitMQ as the <c>rabbitmq</c> provider of <see cref="IMessageTransport"/>: <c>Connection</c> (the
    /// broker's URI, a secret if it carries a password) and optionally <c>Prefetch</c>.
    /// </summary>
    public static void RegisterProviders() =>
        PiperProviders.Register<IMessageTransport>("rabbitmq", settings => new RabbitMqTransport(
            settings.Required("Connection"),
            settings["Prefetch"] is { } prefetch ? ushort.Parse(prefetch, CultureInfo.InvariantCulture) : (ushort)16));

    public async Task SendAsync(Envelope envelope)
    {
        var publisher = await PublisherAsync();
        // Where it goes: a queue through the default exchange, or a topic's exchange.
        var (exchange, routingKey) = envelope.Kind == DestinationKind.Queue ? ("", envelope.Destination) : (envelope.Destination, "");
        await DeclareDestinationAsync(publisher, envelope.Destination, envelope.Kind);
        var wait = envelope.NotBefore is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (wait > TimeSpan.Zero)
            (exchange, routingKey) = ("", await DelayQueueAsync(publisher, envelope.Destination, exchange, routingKey, wait));
        await PublishAsync(publisher, exchange, routingKey, envelope, null);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(Subscription subscription, Func<Envelope, CancellationToken, Task<Handled>> handle)
    {
        var connection = await ConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        var queue = subscription.Kind == DestinationKind.Queue ? subscription.Destination : $"{subscription.Destination}.{subscription.Name}";
        var dead = $"{queue}.dead";
        await DeclareDestinationAsync(channel, subscription.Destination, subscription.Kind);
        if (subscription.Kind == DestinationKind.Topic)
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: subscription.Ordered ? new Dictionary<string, object?> { ["x-single-active-consumer"] = true } : null);
            await channel.QueueBindAsync(queue, subscription.Destination, "");
        }
        await channel.QueueDeclareAsync(dead, durable: true, exclusive: false, autoDelete: false);
        await channel.BasicQosAsync(0, subscription.Ordered ? (ushort)1 : _prefetch, false);

        var stop = new CancellationTokenSource();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var envelope = Read(delivery, subscription);
            Handled handled;
            try
            {
                handled = await handle(envelope, stop.Token);
            }
            catch (Exception error)
            {
                // The bus catches handlers' exceptions; this is the transport's own, so the message is tried again.
                handled = Handled.RetryLater(TimeSpan.FromSeconds(1));
                Console.Error.WriteLine($"Piper.Messaging: a {envelope.Type} from {queue} couldn't be handled: {error.Message}");
            }
            var publisher = await PublisherAsync();
            switch (handled)
            {
                case Handled.RetryHandled { After: var after }:
                    var delay = await DelayQueueAsync(publisher, queue, "", queue, after);
                    await PublishAsync(publisher, "", delay, envelope with { Attempt = envelope.Attempt + 1 }, null);
                    break;
                case Handled.RejectHandled { Reason: var reason }:
                    await PublishAsync(publisher, "", dead, envelope, reason);
                    break;
            }
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        };
        var tag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer);
        return new Consuming(channel, tag, stop);
    }

    /// <summary>The messages dead-lettered by a subscription so far (taking them off its dead-letter queue): for a test.</summary>
    public async Task<IReadOnlyList<DeadLetter>> TakeDeadLettersAsync(Subscription subscription)
    {
        var connection = await ConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var queue = subscription.Kind == DestinationKind.Queue ? subscription.Destination : $"{subscription.Destination}.{subscription.Name}";
        var letters = new List<DeadLetter>();
        while (await channel.BasicGetAsync($"{queue}.dead", autoAck: true) is { } message)
            letters.Add(new DeadLetter(subscription, Read(message.BasicProperties, message.Body, subscription),
                Text(message.BasicProperties.Headers, Reason) ?? ""));
        return letters;
    }

    private async Task<IConnection> ConnectionAsync()
    {
        if (_connection is { } open)
            return open;
        await _opening.WaitAsync();
        try
        {
            return _connection ??= await _factory.CreateConnectionAsync();
        }
        finally
        {
            _opening.Release();
        }
    }

    private async Task<IChannel> PublisherAsync()
    {
        if (_publisher is { } publisher)
            return publisher;
        var connection = await ConnectionAsync();
        await _opening.WaitAsync();
        try
        {
            // Confirms: a publish returns once the broker has the message.
            return _publisher ??= await connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        }
        finally
        {
            _opening.Release();
        }
    }

    /// <summary>A command's queue, or an event's exchange, declared the first time it's used.</summary>
    private async Task DeclareDestinationAsync(IChannel channel, string destination, DestinationKind kind)
    {
        if (_declared.ContainsKey($"{kind}:{destination}"))
            return;
        if (kind == DestinationKind.Queue)
            await channel.QueueDeclareAsync(destination, durable: true, exclusive: false, autoDelete: false);
        else
            await channel.ExchangeDeclareAsync(destination, ExchangeType.Fanout, durable: true);
        _declared[$"{kind}:{destination}"] = true;
    }

    /// <summary>
    /// A queue whose messages wait at least <paramref name="wait"/> (rounded up to a step), then go to
    /// <paramref name="exchange"/> with <paramref name="routingKey"/>: its name. It's deleted after an hour unused.
    /// </summary>
    private async Task<string> DelayQueueAsync(IChannel channel, string target, string exchange, string routingKey, TimeSpan wait)
    {
        var milliseconds = Step((long)Math.Ceiling(wait.TotalMilliseconds));
        var name = $"piper.delay.{target}.{milliseconds}";
        if (_declared.TryAdd($"delay:{name}", true))
        {
            await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = milliseconds,
                ["x-dead-letter-exchange"] = exchange,
                ["x-dead-letter-routing-key"] = routingKey,
                ["x-expires"] = milliseconds + 3_600_000,
            });
        }
        return name;
    }

    /// <summary>The step at or above <paramref name="milliseconds"/>: 1, 1.25, 1.6, 2, 2.5, 3.2, 4, 5, 6.3 or 8 times a power of ten.</summary>
    internal static long Step(long milliseconds)
    {
        ReadOnlySpan<double> steps = [1, 1.25, 1.6, 2, 2.5, 3.2, 4, 5, 6.3, 8];
        for (var scale = 1L; ; scale *= 10)
        {
            foreach (var step in steps)
            {
                var candidate = (long)Math.Round(step * scale);
                if (candidate >= Math.Max(1, milliseconds))
                    return candidate;
            }
        }
    }

    private async Task PublishAsync(IChannel channel, string exchange, string routingKey, Envelope envelope, string? reason)
    {
        var headers = new Dictionary<string, object?> { [Attempt] = envelope.Attempt };
        if (envelope.Key is { } key)
            headers[Key] = key;
        if (envelope.NotBefore is { } at)
            headers[NotBefore] = at.ToUnixTimeMilliseconds();
        if (reason is not null)
            headers[Reason] = reason;
        var properties = new BasicProperties
        {
            MessageId = envelope.Id.ToString(),
            Type = envelope.Type,
            ContentType = "application/json",
            Persistent = true,
            Headers = headers,
        };
        // One publish at a time on the channel, as a channel is for one thread at a time.
        await _publishing.WaitAsync();
        try
        {
            await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, properties, Encoding.UTF8.GetBytes(envelope.Body));
        }
        finally
        {
            _publishing.Release();
        }
    }

    private static Envelope Read(BasicDeliverEventArgs delivery, Subscription subscription) =>
        Read(delivery.BasicProperties, delivery.Body, subscription);

    /// <summary>A delivered message as the bus's envelope, its body copied (RabbitMQ reuses the buffer).</summary>
    private static Envelope Read(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, Subscription subscription)
    {
        var headers = properties.Headers;
        return new Envelope(
            Guid.TryParse(properties.MessageId, out var id) ? id : Guid.Empty,
            subscription.Destination, subscription.Kind, properties.Type ?? "", Encoding.UTF8.GetString(body.Span),
            Text(headers, Key),
            headers?.TryGetValue(NotBefore, out var at) == true && at is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
            headers?.TryGetValue(Attempt, out var attempt) == true && attempt is int n ? n : 1);
    }

    /// <summary>A header's text: RabbitMQ gives strings back as bytes.</summary>
    private static string? Text(IDictionary<string, object?>? headers, string name) =>
        headers?.TryGetValue(name, out var value) == true ? value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => value?.ToString(),
        } : null;

    public async ValueTask DisposeAsync()
    {
        if (_publisher is { } publisher)
            await publisher.DisposeAsync();
        if (_connection is { } connection)
            await connection.DisposeAsync();
    }

    private sealed class Consuming(IChannel channel, string tag, CancellationTokenSource stop) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try
            {
                await channel.BasicCancelAsync(tag);
            }
            catch (Exception)
            {
                // The channel or connection is already gone.
            }
            await channel.DisposeAsync();
            stop.Dispose();
        }
    }
}
