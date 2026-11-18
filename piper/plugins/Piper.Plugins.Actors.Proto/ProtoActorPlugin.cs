using Piper.Plugins;

namespace Piper.Plugins.Actors;

/// <summary>
/// The <c>proto</c> transport for the remote actors plugin's nodes: Proto.Actor's remoting (gRPC) carrying their
/// messages, rather than the built-in HTTP/2. It adds no syntax; a program lists it beside <c>remote-actors</c> and
/// chooses the transport by configuration (<c>Actors:Provider=proto</c>) or in code
/// (<c>PiperNode.StartAsync(ProtoActorTransport(host, port))</c>). The actors are Piper's either way.
/// </summary>
public sealed class ProtoActorPlugin : IPiperPlugin
{
    public string Name => "proto-actor";

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Proto.Remote"] = "1.8.0" };

    /// <summary>ASP.NET Core, whose server carries Proto.Remote's gRPC.</summary>
    public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];

    /// <summary>Piper.Actors.Proto: the transport.</summary>
    public IReadOnlyList<string> References => ["Piper.Actors.Proto.dll"];

    public IReadOnlyList<string> Providers => ["Piper.Actors.Proto.ProtoActorTransport.RegisterProviders"];

    public IReadOnlyList<PluginKeyword> Keywords => [];
}
