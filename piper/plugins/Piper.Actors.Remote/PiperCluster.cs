using System.Collections.Concurrent;
using Proto;
using Proto.Remote;
using Proto.Remote.GrpcNet;

namespace Piper.Actors.Remote;

/// <summary>Starting a node: an actor system that other processes reach over gRPC.</summary>
public static class PiperNode
{
    /// <summary>
    /// Starts a node others reach at <paramref name="host"/>:<paramref name="port"/> (port 0 for any free one). A
    /// loopback host binds to it alone; any other (a service's name, or a pod's IP address) binds to every interface
    /// and tells the others that name.
    /// </summary>
    public static async Task<ActorSystem> StartAsync(string host, int port)
    {
        var config = host is "127.0.0.1" or "localhost"
            ? RemoteConfig.BindToLocalhost(port)
            : RemoteConfig.BindToAllInterfaces(host, port);
        var system = new ActorSystem().WithRemote(config);
        await system.Remote().StartAsync();
        return system;
    }
}

/// <summary>
/// Faults between nodes, for tests: messages from one node to another dropped or delayed, as a network that's split
/// or slow would. They're applied where the remote actors plugin's code sends and asks (<see cref="PiperActorTracing"/>),
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
/// A cluster in one process, for tests: nodes on free loopback ports, each its own actor system talking to the others
/// over gRPC as separate processes would, with faults to inject. Stop a node (a pod deleted), cut one off from the
/// rest or two from each other (a network split), slow the messages to one, and heal them again.
/// <code>
/// let cluster = await PiperCluster.StartAsync(3, node => { Warehouse.Spawn(node, "warehouse", node); });
/// cluster.Isolate(2);                // nodes 0 and 1 can't reach it, nor it them
/// …
/// cluster.Heal();
/// await cluster.StopAsync();
/// </code>
/// </summary>
public sealed class PiperCluster : IAsyncDisposable
{
    private readonly List<ActorSystem> _nodes;
    private readonly Action<ActorSystem>? _setup;
    private readonly HashSet<int> _stopped = [];

    private PiperCluster(List<ActorSystem> nodes, Action<ActorSystem>? setup)
    {
        _nodes = nodes;
        _setup = setup;
    }

    /// <summary>Starts <paramref name="count"/> nodes, calling <paramref name="setup"/> on each (to spawn its actors).</summary>
    public static async Task<PiperCluster> StartAsync(int count, Action<ActorSystem>? setup = null)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), "A cluster has a node at least.");
        var nodes = await Task.WhenAll(Enumerable.Range(0, count).Select(_ => PiperNode.StartAsync("127.0.0.1", 0)));
        foreach (var node in nodes)
            setup?.Invoke(node);
        return new PiperCluster([.. nodes], setup);
    }

    /// <summary>The nodes, in the order they were started (stopped ones included, so the numbers stay).</summary>
    public IReadOnlyList<ActorSystem> Nodes => _nodes;

    /// <summary>The nodes' addresses (host:port), in the same order.</summary>
    public IReadOnlyList<string> Addresses => [.. _nodes.Select(n => n.Address)];

    /// <summary>Node <paramref name="node"/>, as <see cref="Nodes"/> numbers them.</summary>
    public ActorSystem this[int node] => _nodes[node];

    /// <summary>Starts another node (set up as the others were), as a pod added: its number is the next.</summary>
    public async Task<ActorSystem> AddNodeAsync()
    {
        var node = await PiperNode.StartAsync("127.0.0.1", 0);
        _setup?.Invoke(node);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Stops node <paramref name="node"/>, as a pod deleted: messages to it are lost, and asks to it fail.</summary>
    public async Task StopAsync(int node)
    {
        if (!_stopped.Add(node))
            return;
        PiperFaults.Heal(_nodes[node].Address);
        await _nodes[node].Remote().ShutdownAsync(true);
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
