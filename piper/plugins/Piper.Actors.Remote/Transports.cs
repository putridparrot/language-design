using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Piper.Actors.Remote;

/// <summary>
/// How a node's frames reach other nodes, and theirs it: the provider an <see cref="ActorSystem"/> runs on, chosen by
/// configuration (<c>Actors:Provider</c>: <c>memory</c>, the local one; <c>http</c>; or one a plugin registers) or in
/// code. A transport only carries bytes: what they mean is the actor system's, the same on every transport.
/// </summary>
public interface IActorTransport : IAsyncDisposable
{
    /// <summary>
    /// Starts taking frames from other nodes, handing each to <paramref name="receive"/> in the order its sender sent
    /// them; answers this node's address (host:port), which the others send to.
    /// </summary>
    Task<string> StartAsync(Action<ReadOnlyMemory<byte>> receive);

    /// <summary>
    /// Sends <paramref name="frame"/> to the node at <paramref name="address"/>, after those sent to it before, at most
    /// once: it's lost if that node can't be reached. Never blocks, and never throws for an unreachable node.
    /// </summary>
    void Send(string address, ReadOnlyMemory<byte> frame);
}

/// <summary>
/// Nodes in one process, for tests and for running a cluster's code on one machine: each frame is copied to the other
/// node's inbox, so messages still cross as JSON, as they would between processes. A node that has stopped is
/// unreachable: frames to it are lost. Addresses are <c>memory:1</c>, <c>memory:2</c>, ….
/// </summary>
public sealed class MemoryActorTransport : IActorTransport
{
    private static readonly ConcurrentDictionary<string, MemoryActorTransport> Nodes = new();
    private static int _next;

    private readonly Channel<byte[]> _inbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private string? _address;
    private Task _pump = Task.CompletedTask;

    public Task<string> StartAsync(Action<ReadOnlyMemory<byte>> receive)
    {
        _address = $"memory:{Interlocked.Increment(ref _next)}";
        Nodes[_address] = this;
        _pump = Task.Run(async () =>
        {
            await foreach (var frame in _inbox.Reader.ReadAllAsync())
            {
                try
                {
                    receive(frame);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"Piper.Actors: {error.Message}");
                }
            }
        });
        return Task.FromResult(_address);
    }

    public void Send(string address, ReadOnlyMemory<byte> frame)
    {
        if (Nodes.TryGetValue(address, out var node))
            node._inbox.Writer.TryWrite(frame.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_address is not null)
            Nodes.TryRemove(_address, out _);
        _inbox.Writer.TryComplete();
        await _pump;
    }
}

/// <summary>
/// Nodes talking over HTTP/2, with nothing beyond .NET's own ASP.NET Core: each node serves <see cref="Path"/> on
/// Kestrel, and sends to each other node on one long-lived request whose body is its frames, each after its length (four
/// bytes, big-endian). So frames to a node arrive in the order they were sent. A node that can't be reached loses what
/// was sent to it meanwhile; the next frame tries again. Answers come back on the other node's own request to this one,
/// so every node must be reachable at its address, as it is in a cluster.
/// </summary>
/// <param name="host">
/// The name others reach this node by. A loopback host (127.0.0.1, localhost) binds to loopback alone; any other (a
/// service's name, or a pod's IP address) binds to every interface and tells the others that name.
/// </param>
/// <param name="port">The port, or 0 for any free one.</param>
public sealed class HttpActorTransport(string host, int port) : IActorTransport
{
    /// <summary>Where a node takes frames from others.</summary>
    public const string Path = "/piper/actors";

    /// <summary>The longest frame taken: a message larger than this is refused, rather than read into memory.</summary>
    private const int MaxFrame = 64 * 1024 * 1024;

    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly HttpClient _client = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private WebApplication? _app;

    public async Task<string> StartAsync(Action<ReadOnlyMemory<byte>> receive)
    {
        bool loopback = host is "127.0.0.1" or "localhost" or "::1";
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            // A peer's request lasts as long as both nodes do, and is idle between messages.
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.Limits.MaxRequestBodySize = null;
            void Http2(ListenOptions listen) => listen.Protocols = HttpProtocols.Http2;
            if (loopback)
                kestrel.Listen(IPAddress.Loopback, port, Http2);
            else
                kestrel.ListenAnyIP(port, Http2);
        });
        _app = builder.Build();
        _app.Run(context => AcceptAsync(context, receive));
        await _app.StartAsync();
        var bound = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return $"{(loopback ? "127.0.0.1" : host)}:{new Uri(bound.Replace("[::]", "localhost")).Port}";
    }

    /// <summary>Another node's request: its frames, until it stops (or this node does).</summary>
    private async Task AcceptAsync(HttpContext context, Action<ReadOnlyMemory<byte>> receive)
    {
        if (context.Request.Path != Path || !HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        var body = context.Request.BodyReader;
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(stop.Token);
                var buffer = read.Buffer;
                while (TryTake(ref buffer, out var frame))
                    receive(frame);
                body.AdvanceTo(buffer.Start, buffer.End);
                if (read.IsCompleted)
                    break;
            }
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The other node went away mid-frame.
        }
    }

    private static bool TryTake(ref ReadOnlySequence<byte> buffer, out byte[] frame)
    {
        frame = [];
        if (buffer.Length < 4)
            return false;
        Span<byte> prefix = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(prefix);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is < 0 or > MaxFrame)
            throw new InvalidDataException($"A frame of {length} bytes is more than a node takes ({MaxFrame}).");
        if (buffer.Length < 4 + length)
            return false;
        frame = buffer.Slice(4, length).ToArray();
        buffer = buffer.Slice(4 + length);
        return true;
    }

    public void Send(string address, ReadOnlyMemory<byte> frame)
    {
        if (_stopping.IsCancellationRequested)
            return;
        _peers.GetOrAdd(address, a => new Peer(this, a)).Queue.Writer.TryWrite(frame);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
            return;
        _stopping.Cancel();
        if (_app is not null)
        {
            // Without waiting for other nodes' requests to finish: they last as long as those nodes.
            await _app.StopAsync(new CancellationToken(canceled: true));
            await _app.DisposeAsync();
        }
        await Task.WhenAll(_peers.Values.Select(p => p.Pump));
        _client.Dispose();
    }

    /// <summary>Another node, as this one sends to it: what's waiting to go, and the loop sending it.</summary>
    private sealed class Peer
    {
        public readonly Channel<ReadOnlyMemory<byte>> Queue = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

        public readonly Task Pump;

        public Peer(HttpActorTransport transport, string address) => Pump = Task.Run(() => PumpAsync(transport, address));

        /// <summary>
        /// Once there's something to send, opens a request to the node and streams frames on it while it lasts. When it
        /// fails (the node is down, or went away) what was waiting is lost, as at-most-once delivery allows, and the next
        /// frame opens another.
        /// </summary>
        private async Task PumpAsync(HttpActorTransport transport, string address)
        {
            var token = transport._stopping.Token;
            try
            {
                while (await Queue.Reader.WaitToReadAsync(token))
                {
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{address}{Path}")
                        {
                            Version = HttpVersion.Version20,
                            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                            Content = new FrameContent(Queue.Reader),
                        };
                        using var response = await transport._client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        while (Queue.Reader.TryRead(out _))
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>A request body that's the peer's frames as they're sent, each after its length, flushed as they come.</summary>
    private sealed class FrameContent(ChannelReader<ReadOnlyMemory<byte>> frames) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var prefix = new byte[4];
            while (await frames.WaitToReadAsync(cancellationToken))
            {
                while (frames.TryRead(out var frame))
                {
                    BinaryPrimitives.WriteInt32BigEndian(prefix, frame.Length);
                    await stream.WriteAsync(prefix, cancellationToken);
                    await stream.WriteAsync(frame, cancellationToken);
                }
                await stream.FlushAsync(cancellationToken);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
