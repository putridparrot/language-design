using System.Collections.Concurrent;
using Piper.Runtime;

namespace Piper.Actors.Remote;

/// <summary>Starting a node: an actor system that other processes, or other nodes in this one, reach.</summary>
public static class PiperNode
{
    /// <summary>
    /// Starts a node others reach over the network at <paramref name="host"/>:<paramref name="port"/> (port 0 for any
    /// free one), on the <c>http</c> transport. A loopback host binds to it alone; any other (a service's name, or a pod's
    /// IP address) binds to every interface and tells the others that name.
    /// </summary>
    public static Task<ActorSystem> StartAsync(string host, int port) => ActorSystem.StartAsync(new HttpActorTransport(host, port));

    /// <summary>Starts a node on <paramref name="transport"/>: one chosen by configuration, or a plugin's.</summary>
    public static Task<ActorSystem> StartAsync(IActorTransport transport) => ActorSystem.StartAsync(transport);

    /// <summary>Starts a node only other nodes in this process reach (the <c>memory</c> transport).</summary>
    public static Task<ActorSystem> StartInMemoryAsync() => ActorSystem.StartAsync(new MemoryActorTransport());

    /// <summary>
    /// The transports a program can choose by configuration (<c>Actors:Provider</c>, read with a server's or host's
    /// <c>provider</c> clause or <c>PiperHost.Provider</c>): <c>memory</c>, the local one, and <c>http</c>, which takes
    /// <c>Host</c> (127.0.0.1 if it isn't set) and <c>Port</c> (any free one if it isn't).
    /// </summary>
    public static void RegisterProviders()
    {
        PiperProviders.Register<IActorTransport>("memory", _ => new MemoryActorTransport(), local: true);
        PiperProviders.Register<IActorTransport>("http", settings => new HttpActorTransport(settings["Host"] ?? "127.0.0.1", Port(settings)));
    }

    private static int Port(ProviderSettings settings) => settings["Port"] switch
    {
        null => 0,
        var text when int.TryParse(text, out var port) && port is >= 0 and <= 65535 => port,
        var text => throw new ProviderException($"{settings.Section}:Port is '{text}', which isn't a port number."),
    };
}

/// <summary>
/// Faults between nodes, for tests: messages from one node to another dropped or delayed, as a network that's split
/// or slow would. They are applied where a node sends and asks (<see cref="ActorSystem.Send{TMessage}"/>), cluster heartbeats included,
/// so they affect only messages to and from the nodes they name, by address, and tests running at once on other
/// nodes aren't touched. A dropped tell is lost; a dropped ask times out, as one whose message was lost does.
/// </summary>
public static class PiperFaults
{
    /// <summary>A fault: messages from <c>From</c> to <c>To</c> (null for any node) dropped, or delayed by <c>Delay</c>.</summary>
    private sealed record Fault(string? From, string? To, TimeSpan? Delay);

    private static readonly ConcurrentDictionary<Fault, byte> Faults = new();

    /// <summary>What happens to a message from <paramref name="from"/> to <paramref name="to"/>: null for nothing, zero to drop it, or its delay.</summary>
    internal static TimeSpan? For(string from, string to)
    {
        // A node's messages to its own actors don't cross the network.
        if (Faults.IsEmpty || from == to)
            return null;
        TimeSpan? delay = null;
        foreach (var fault in Faults.Keys)
        {
            if ((fault.From is null || fault.From == from) && (fault.To is null || fault.To == to) && !(fault.From is null && fault.To is null))
            {
                if (fault.Delay is not { } by)
                    return TimeSpan.Zero;
                delay = delay is { } longest && longest > by ? longest : by;
            }
        }
        return delay;
    }

    /// <summary>Drops messages from <paramref name="from"/> to <paramref name="to"/> (addresses; null for any node, but not both).</summary>
    public static void Drop(string? from, string? to) => Faults[new Fault(from, to, null)] = 0;

    /// <summary>Delays messages from <paramref name="from"/> to <paramref name="to"/> by <paramref name="by"/>.</summary>
    public static void Delay(string? from, string? to, TimeSpan by) => Faults[new Fault(from, to, by)] = 0;

    /// <summary>Removes every fault involving <paramref name="address"/>.</summary>
    public static void Heal(string address)
    {
        foreach (var fault in Faults.Keys.Where(f => f.From == address || f.To == address))
            Faults.TryRemove(fault, out _);
    }
}

/// <summary>
/// A cluster in one process, for tests: nodes each with their own actor system, talking to the others as separate
/// processes would (their messages crossing as JSON), with faults to inject. Stop a node (a pod deleted), cut one off
/// from the rest or two from each other (a network split), slow the messages to one, and heal them again. The nodes
/// talk in memory (<see cref="StartAsync"/>), or over HTTP/2 on loopback ports (<see cref="StartOverNetworkAsync"/>).
/// The nodes have joined a <see cref="Remote.Cluster"/> of their own (each node's <c>Cluster</c>), pinging each other
/// every tenth of a second and taking one unheard for a second to be down, so actors in the cluster move between them
/// as the faults come and go.
/// <code>
/// let cluster = await PiperCluster.StartAsync(3, node => { Product.HostInCluster(node.Cluster, (n, name, sku) => Product.Spawn(n, name, store, sku)); });
/// cluster.Isolate(2);                // nodes 0 and 1 can't reach it, nor it them
/// await cluster.WaitForMembersAsync(0, 2);
/// …
/// cluster.Heal();
/// await cluster.StopAsync();
/// </code>
/// </summary>
public sealed class PiperCluster : IAsyncDisposable
{
    /// <summary>A test's cluster's pace: a ping every tenth of a second, and down after a second unheard.</summary>
    public static readonly ClusterOptions Options = new(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));

    private readonly List<ActorSystem> _nodes;
    private readonly Action<ActorSystem>? _setup;
    private readonly Func<Task<ActorSystem>> _start;
    private readonly string _name;
    private readonly ClusterOptions _options;
    private readonly HashSet<int> _stopped = [];

    private PiperCluster(List<ActorSystem> nodes, Action<ActorSystem>? setup, Func<Task<ActorSystem>> start, string name, ClusterOptions options)
    {
        _nodes = nodes;
        _setup = setup;
        _start = start;
        _name = name;
        _options = options;
    }

    /// <summary>
    /// Starts <paramref name="count"/> nodes talking in memory, calling <paramref name="setup"/> on each (to spawn its
    /// actors). Their cluster's pace is <paramref name="options"/>, <see cref="Options"/> if not given: a test of faults
    /// on plain asks, which shouldn't have the cluster finding nodes down mid-test however slow the machine, can give
    /// one with a long <c>DownAfter</c>.
    /// </summary>
    public static Task<PiperCluster> StartAsync(int count, Action<ActorSystem>? setup = null, ClusterOptions? options = null) =>
        StartAsync(count, setup, PiperNode.StartInMemoryAsync, options ?? Options);

    /// <summary>Starts <paramref name="count"/> nodes talking over HTTP/2 on free loopback ports, calling <paramref name="setup"/> on each.</summary>
    public static Task<PiperCluster> StartOverNetworkAsync(int count, Action<ActorSystem>? setup = null, ClusterOptions? options = null) =>
        StartAsync(count, setup, () => PiperNode.StartAsync("127.0.0.1", 0), options ?? Options);

    private static async Task<PiperCluster> StartAsync(int count, Action<ActorSystem>? setup, Func<Task<ActorSystem>> start, ClusterOptions options)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "A cluster has a node at least.");
        var nodes = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => start()));
        // Its own cluster, so tests running at once don't meet. Joined one at a time, so each pings those before it,
        // and they all know each other when this answers.
        var name = $"test-{Guid.NewGuid():N}";
        foreach (var node in nodes)
            await Remote.Cluster.JoinAsync(node, new MemoryClusterMembership(), name, options);
        foreach (var node in nodes)
            setup?.Invoke(node);
        return new PiperCluster([.. nodes], setup, start, name, options);
    }

    /// <summary>The nodes, in the order they were started (stopped ones included, so the numbers stay).</summary>
    public IReadOnlyList<ActorSystem> Nodes => _nodes;

    /// <summary>The nodes' addresses, in the same order.</summary>
    public IReadOnlyList<string> Addresses => [.. _nodes.Select(n => n.Address)];

    /// <summary>Node <paramref name="node"/>, as <see cref="Nodes"/> numbers them.</summary>
    public ActorSystem this[int node] => _nodes[node];

    /// <summary>Starts another node (set up as the others were), as a pod added: its number is the next.</summary>
    public async Task<ActorSystem> AddNodeAsync()
    {
        var node = await _start();
        await Remote.Cluster.JoinAsync(node, new MemoryClusterMembership(), _name, _options);
        _setup?.Invoke(node);
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Stops node <paramref name="node"/>, as a pod deleted: it leaves the cluster (handing its actors over), then
    /// messages to it are lost, and asks to it time out.
    /// </summary>
    public async Task StopAsync(int node)
    {
        if (!_stopped.Add(node))
            return;
        PiperFaults.Heal(_nodes[node].Address);
        await _nodes[node].StopAsync();
    }

    /// <summary>
    /// Stops node <paramref name="node"/> as one that crashed: without leaving the cluster, so the others find it gone
    /// only when it stops answering their pings.
    /// </summary>
    public async Task KillAsync(int node)
    {
        if (!_stopped.Add(node))
            return;
        PiperFaults.Heal(_nodes[node].Address);
        await _nodes[node].AbortAsync();
    }

    /// <summary>
    /// Waits until node <paramref name="node"/> sees <paramref name="count"/> members up (itself included), for up to
    /// <paramref name="within"/> (five seconds if not given): a <see cref="TimeoutException"/> if it doesn't.
    /// </summary>
    public async Task WaitForMembersAsync(int node, int count, TimeSpan? within = null)
    {
        var cluster = _nodes[node].Cluster;
        var limit = within ?? TimeSpan.FromSeconds(5);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (cluster.Members.Count != count)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > limit)
                throw new TimeoutException($"Node {node} sees {cluster.Members.Count} members up, not {count}, after {limit}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Cuts node <paramref name="node"/> off: nothing it sends reaches the others, nor theirs it.</summary>
    public void Isolate(int node)
    {
        PiperFaults.Drop(_nodes[node].Address, null);
        PiperFaults.Drop(null, _nodes[node].Address);
    }

    /// <summary>Splits nodes <paramref name="a"/> and <paramref name="b"/>: neither's messages reach the other.</summary>
    public void Partition(int a, int b)
    {
        Drop(a, b);
        Drop(b, a);
    }

    /// <summary>Drops the messages node <paramref name="from"/> sends node <paramref name="to"/>, but not the other way.</summary>
    public void Drop(int from, int to) => PiperFaults.Drop(_nodes[from].Address, _nodes[to].Address);

    /// <summary>Delays every message to node <paramref name="node"/> by <paramref name="by"/>, as a slow network or node.</summary>
    public void Slow(int node, TimeSpan by) => PiperFaults.Delay(null, _nodes[node].Address, by);

    /// <summary>Removes every fault: the nodes still running reach each other again.</summary>
    public void Heal()
    {
        foreach (var node in _nodes)
            PiperFaults.Heal(node.Address);
    }

    /// <summary>Heals the faults and stops every node still running.</summary>
    public async Task StopAsync()
    {
        Heal();
        for (var node = 0; node < _nodes.Count; node++)
            await StopAsync(node);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
