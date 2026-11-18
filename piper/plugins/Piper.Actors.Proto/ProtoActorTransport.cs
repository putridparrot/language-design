using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Piper.Actors.Remote;
using Piper.Runtime;
using Proto;
using Proto.Remote;
using Proto.Remote.GrpcNet;

namespace Piper.Actors.Proto;

/// <summary>
/// Nodes talking over Proto.Actor's remoting (gRPC), for a program that chooses it (<c>Actors:Provider=proto</c>, or
/// <c>PiperNode.StartAsync(ProtoActorTransport(host, port))</c>). Each node is a Proto.Actor system with one actor,
/// <c>piper-frames</c>, which takes the frames other nodes send it, as protobuf bytes, and hands them on in order. The
/// actors themselves are Piper.Actors.Remote's: Proto.Actor only carries their messages, so they behave as on any
/// other transport. Delivery is Proto.Remote's: in order between two nodes, and lost if one can't be reached.
/// </summary>
/// <param name="host">
/// The name others reach this node by. A loopback host (127.0.0.1, localhost) binds to loopback alone; any other (a
/// service's name, or a pod's IP address) binds to every interface and tells the others that name.
/// </param>
/// <param name="port">The port, or 0 for any free one.</param>
public sealed class ProtoActorTransport(string host, int port) : IActorTransport
{
    /// <summary>The Proto.Actor actor on each node that takes frames from the others.</summary>
    private const string Inbox = "piper-frames";

    private global::Proto.ActorSystem? _system;

    /// <summary>
    /// Makes <c>proto</c> a transport a program can choose by configuration: it takes <c>Host</c> (127.0.0.1 if it isn't
    /// set) and <c>Port</c> (any free one if it isn't), as <c>http</c> does.
    /// </summary>
    public static void RegisterProviders() =>
        PiperProviders.Register<IActorTransport>("proto", settings => new ProtoActorTransport(settings["Host"] ?? "127.0.0.1", Port(settings)));

    private static int Port(ProviderSettings settings) => settings["Port"] switch
    {
        null => 0,
        var text when int.TryParse(text, out var port) && port is >= 0 and <= 65535 => port,
        var text => throw new ProviderException($"{settings.Section}:Port is '{text}', which isn't a port number."),
    };

    public async Task<string> StartAsync(Action<ReadOnlyMemory<byte>> receive)
    {
        var config = (host is "127.0.0.1" or "localhost" ? RemoteConfig.BindToLocalhost(port) : RemoteConfig.BindToAllInterfaces(host, port))
            .WithProtoMessages(WrappersReflection.Descriptor);
        _system = new global::Proto.ActorSystem().WithRemote(config);
        await _system.Remote().StartAsync();
        _system.Root.SpawnNamed(Props.FromFunc(context =>
        {
            if (context.Message is BytesValue frame)
                receive(frame.Value.Memory);
            return Task.CompletedTask;
        }), Inbox);
        return _system.Address;
    }

    public void Send(string address, ReadOnlyMemory<byte> frame) =>
        _system?.Root.Send(PID.FromAddress(address, Inbox), new BytesValue { Value = ByteString.CopyFrom(frame.Span) });

    public async ValueTask DisposeAsync()
    {
        if (_system is { } system)
        {
            _system = null;
            await system.Remote().ShutdownAsync(true);
        }
    }
}
