using Piper.Plugins;

namespace Piper.Plugins.Messaging;

/// <summary>
/// The <c>rabbitmq</c> transport for the messaging plugin's bus: commands, events, delays, retries and dead letters on
/// RabbitMQ, over its official client (RabbitMQ.Client, open source). It adds no syntax; a program lists it beside
/// <c>messaging</c> and chooses the transport by configuration (<c>Messaging:Provider=rabbitmq</c>, with
/// <c>Messaging:Connection</c>) or in code (<c>Bus(RabbitMqTransport("amqp://…"))</c>). The messages are Piper's either way.
/// </summary>
public sealed class RabbitMqPlugin : IPiperPlugin
{
    public string Name => "rabbitmq";

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["RabbitMQ.Client"] = "7.1.2" };

    /// <summary>Piper.Messaging.RabbitMq: the transport.</summary>
    public IReadOnlyList<string> References => ["Piper.Messaging.RabbitMq.dll"];

    public IReadOnlyList<string> Providers => ["Piper.Messaging.RabbitMq.RabbitMqTransport.RegisterProviders"];

    public IReadOnlyList<PluginKeyword> Keywords => [];
}
