using System.Text;
using Piper.Runtime;

namespace Piper.Actors.Remote;

/// <summary>
/// What a simulated network does to the frames it carries, each choice made by the simulation's seed: how long each
/// takes (between <paramref name="MinLatency"/> and <paramref name="MaxLatency"/>), how likely it is to be lost
/// (<paramref name="Drop"/>) or delivered twice (<paramref name="Duplicate"/>, which Piper's transports never do, for
/// protocols that must cope with networks that might), and whether frames between two nodes may overtake each other
/// (<paramref name="Reorder"/>; Piper's transports keep each sender's order).
/// </summary>
public sealed record NetworkFaults(double Drop, double Duplicate, TimeSpan MinLatency, TimeSpan MaxLatency, bool Reorder)
{
    /// <summary>A network that loses nothing, each frame taking 1 to 10 milliseconds, in order between each two nodes.</summary>
    public static readonly NetworkFaults None = new(0, 0, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10), false);

    /// <summary>As <see cref="None"/>, losing each frame with probability <paramref name="drop"/>.</summary>
    public static NetworkFaults Lossy(double drop) => None with { Drop = drop };
}

/// <summary>
/// The network of a simulation (<see cref="PiperSimulation"/>): nodes' transports (<see cref="SimulatedTransport"/>)
/// hand it frames, and it delivers each after a latency it chooses, or loses it, by the simulation's seed. Nodes can be
/// cut off from each other (<see cref="Isolate"/>, <see cref="Partition"/>) and joined again (<see cref="Heal"/>); a
/// frame in flight when its nodes are cut off is lost. What it carries goes in the simulation's timeline (but not the
/// cluster's pings, unless <see cref="LogPings"/>).
/// </summary>
public sealed class SimulatedNetwork(PiperSimulation simulation, NetworkFaults? faults = null)
{
    private readonly Dictionary<string, SimulatedTransport> _nodes = [];
    private readonly HashSet<(string, string)> _cut = [];
    private readonly Dictionary<(string, string), long> _last = [];
    private int _next;

    public PiperSimulation Simulation => simulation;

    public NetworkFaults Faults { get; set; } = faults ?? NetworkFaults.None;

    /// <summary>Whether the clusters' pings between nodes go in the timeline too (there are many).</summary>
    public bool LogPings { get; set; }

    /// <summary>A transport on this network, for a new node: <c>sim:1</c>, <c>sim:2</c>, …, or <paramref name="address"/>
    /// (a node restarting where it was).</summary>
    public SimulatedTransport Transport(string? address = null) => new(this, address);

    /// <summary>Cuts <paramref name="address"/> off from every other node.</summary>
    public void Isolate(string address)
    {
        foreach (var other in _nodes.Keys.Order(StringComparer.Ordinal).Where(a => a != address))
            Cut(address, other);
        simulation.Log($"network: {address} cut off");
    }

    /// <summary>Splits the network in two: the nodes at <paramref name="side"/>, and the rest.</summary>
    public void Partition(IEnumerable<string> side)
    {
        var one = side.ToHashSet();
        foreach (var a in one)
            foreach (var b in _nodes.Keys.Where(b => !one.Contains(b)))
                Cut(a, b);
        simulation.Log($"network: split, {string.Join(", ", one.Order(StringComparer.Ordinal))} apart from the rest");
    }

    /// <summary>Joins every node to every other again.</summary>
    public void Heal()
    {
        _cut.Clear();
        simulation.Log("network: healed");
    }

    private void Cut(string a, string b)
    {
        _cut.Add((a, b));
        _cut.Add((b, a));
    }

    internal string Attach(SimulatedTransport transport, string? address)
    {
        address ??= $"sim:{++_next}";
        if (_nodes.ContainsKey(address))
            throw new InvalidOperationException($"A node at {address} is already on the simulated network.");
        _nodes[address] = transport;
        return address;
    }

    internal void Detach(string address) => _nodes.Remove(address);

    internal void Send(string from, string to, ReadOnlyMemory<byte> frame)
    {
        var bytes = frame.ToArray();
        var log = LogPings || !IsPing(bytes);
        if (!_nodes.ContainsKey(to) || _cut.Contains((from, to)) || simulation.Chance(Faults.Drop))
        {
            if (log)
                simulation.Log($"{from} -> {to}: {Describe(bytes)}  LOST");
            return;
        }
        var copies = simulation.Chance(Faults.Duplicate) ? 2 : 1;
        for (var copy = 0; copy < copies; copy++)
        {
            var due = simulation.Elapsed.Ticks + simulation.Between(Faults.MinLatency, Faults.MaxLatency).Ticks;
            // In order between each two nodes, as Piper's transports are: no frame overtakes one sent before it.
            if (!Faults.Reorder && _last.TryGetValue((from, to), out var last) && due <= last)
                due = last + 1;
            _last[(from, to)] = due;
            if (log)
                simulation.Log($"{from} -> {to}: {Describe(bytes)}{(copy > 0 ? "  DUPLICATED" : "")}");
            _ = DeliverAsync(from, to, bytes, TimeSpan.FromTicks(due - simulation.Elapsed.Ticks), log);
        }
    }

    private async Task DeliverAsync(string from, string to, byte[] bytes, TimeSpan after, bool log)
    {
        await simulation.Delay(after);
        if (!_nodes.TryGetValue(to, out var node) || _cut.Contains((from, to)))
        {
            if (log)
                simulation.Log($"{from} -> {to}: {Describe(bytes)}  LOST in flight");
            return;
        }
        node.Deliver(bytes);
    }

    private static bool IsPing(byte[] bytes)
    {
        try
        {
            return Frame.Decode(bytes).To == Cluster.ActorName;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A frame as the timeline shows it: its kind, its actor, and the start of its JSON.</summary>
    private static string Describe(byte[] bytes)
    {
        try
        {
            var frame = Frame.Decode(bytes);
            var json = Encoding.UTF8.GetString(frame.Payload.Span);
            if (json.Length > 80)
                json = json[..77] + "...";
            return $"{frame.Kind} {(frame.To.Length > 0 ? frame.To + " " : "")}{json}".TrimEnd();
        }
        catch (Exception)
        {
            return $"{bytes.Length} bytes";
        }
    }
}

/// <summary>A node's transport on a <see cref="SimulatedNetwork"/>.</summary>
public sealed class SimulatedTransport : IActorTransport
{
    private readonly SimulatedNetwork _network;
    private readonly string? _wanted;
    private Action<ReadOnlyMemory<byte>>? _receive;
    private string? _address;

    internal SimulatedTransport(SimulatedNetwork network, string? address)
    {
        _network = network;
        _wanted = address;
    }

    public Task<string> StartAsync(Action<ReadOnlyMemory<byte>> receive)
    {
        _receive = receive;
        _address = _network.Attach(this, _wanted);
        return Task.FromResult(_address);
    }

    public void Send(string address, ReadOnlyMemory<byte> frame)
    {
        if (_address is not null)
            _network.Send(_address, address, frame);
    }

    internal void Deliver(byte[] frame)
    {
        try
        {
            _receive?.Invoke(frame);
        }
        catch (Exception error)
        {
            _network.Simulation.Log($"{_address}: a frame couldn't be read: {error.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_address is not null)
            _network.Detach(_address);
        _address = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A cluster of nodes in a simulation, as <see cref="PiperCluster"/> is in real time: each node on the simulation's
/// network, joined to one cluster, set up by the test (hosting its kinds of actor), with faults to inject: a node
/// stopped, crashed or restarted, the network split. Everything it does is chosen by the simulation's seed.
/// <code>
/// PiperSimulation.Explore(200, async sim => {
///     let cluster = await SimulatedCluster.StartAsync(sim, 3, node => Tally.HostInCluster(node.Cluster, …));
///     await Tally.InCluster(cluster[0], "tea").Add(1);
///     await cluster.CrashAsync(1);
///     …
/// });
/// </code>
/// Its nodes are stopped when the simulation's body finishes.
/// </summary>
public sealed class SimulatedCluster
{
    private readonly PiperSimulation _simulation;
    private readonly Action<ActorSystem> _setup;
    private readonly ClusterOptions _options;
    private readonly Members _members = new();
    private readonly List<ActorSystem> _nodes = [];
    private readonly HashSet<int> _down = [];
    private Task _chaos = Task.CompletedTask;
    private bool _chaotic;

    private SimulatedCluster(PiperSimulation simulation, SimulatedNetwork network, Action<ActorSystem> setup, ClusterOptions options)
    {
        _simulation = simulation;
        Network = network;
        _setup = setup;
        _options = options;
    }

    /// <summary>
    /// Starts <paramref name="count"/> nodes in <paramref name="simulation"/>, on a network with
    /// <paramref name="faults"/> (none by default), each joining the cluster before <paramref name="setup"/> runs.
    /// </summary>
    public static async Task<SimulatedCluster> StartAsync(PiperSimulation simulation, int count, Action<ActorSystem> setup,
        NetworkFaults? faults = null, ClusterOptions? options = null)
    {
        var cluster = new SimulatedCluster(simulation, new SimulatedNetwork(simulation, faults), setup, options ?? ClusterOptions.Default);
        simulation.AtEnd(cluster.StopAllAsync);
        for (var i = 0; i < count; i++)
            await cluster.AddNodeAsync();
        return cluster;
    }

    /// <summary>The same, in the simulation the code is running in (a <c>simulate</c> test's).</summary>
    public static Task<SimulatedCluster> StartAsync(int count, Action<ActorSystem> setup, NetworkFaults? faults = null, ClusterOptions? options = null) =>
        StartAsync(PiperSimulation.Running, count, setup, faults, options);

    public SimulatedNetwork Network { get; }

    /// <summary>The nodes, in the order they were started (a crashed or stopped one stays, at its index).</summary>
    public ActorSystem this[int node] => _nodes[node];

    public int Count => _nodes.Count;

    public IReadOnlyList<string> Addresses => [.. _nodes.Select(n => n.Address)];

    /// <summary>Whether the node is running (not stopped or crashed).</summary>
    public bool IsUp(int node) => !_down.Contains(node);

    /// <summary>Starts another node, which joins and takes its share: its index.</summary>
    public async Task<int> AddNodeAsync()
    {
        _nodes.Add(await StartNodeAsync(null));
        return _nodes.Count - 1;
    }

    /// <summary>Stops a node as Kubernetes would: it leaves the cluster first, handing its actors over.</summary>
    public async Task StopAsync(int node)
    {
        if (!_down.Add(node))
            return;
        _simulation.Log($"{_nodes[node].Address}: stopping");
        _members.Remove(_nodes[node].Address);
        await _nodes[node].StopAsync();
    }

    /// <summary>Crashes a node: it stops at once, handing nothing over; the others find it gone when it stops answering.</summary>
    public async Task CrashAsync(int node)
    {
        if (!_down.Add(node))
            return;
        _simulation.Log($"{_nodes[node].Address}: crashed");
        await _nodes[node].AbortAsync();
    }

    /// <summary>Starts a stopped or crashed node again, at the same address, as a pod restarting under its name would:
    /// a new process, with nothing kept but what's in the actors' store.</summary>
    public async Task RestartAsync(int node)
    {
        if (!_down.Remove(node))
            throw new InvalidOperationException($"Node {node} ({_nodes[node].Address}) is running.");
        _simulation.Log($"{_nodes[node].Address}: restarting");
        _nodes[node] = await StartNodeAsync(_nodes[node].Address);
    }

    /// <summary>Cuts a node off from the others.</summary>
    public void Isolate(int node) => Network.Isolate(_nodes[node].Address);

    /// <summary>Splits the network: the nodes at <paramref name="side"/> apart from the rest.</summary>
    public void Partition(params int[] side) => Network.Partition(side.Select(i => _nodes[i].Address));

    /// <summary>Joins the network up again.</summary>
    public void Heal() => Network.Heal();

    /// <summary>
    /// Waits until <paramref name="node"/> sees <paramref name="count"/> members up, for up to <paramref name="within"/>
    /// of simulated time (a minute): whether it did.
    /// </summary>
    public async Task<bool> WaitForMembersAsync(int node, int count, TimeSpan? within = null)
    {
        var until = _simulation.Elapsed + (within ?? TimeSpan.FromMinutes(1));
        while (_nodes[node].Cluster.Members.Count != count)
        {
            if (_simulation.Elapsed >= until)
                return false;
            await _simulation.Delay(TimeSpan.FromMilliseconds(50));
        }
        return true;
    }

    /// <summary>
    /// Waits until every running node sees the same members (every running node), for up to <paramref name="within"/>
    /// of simulated time (a minute): whether they did.
    /// </summary>
    public async Task<bool> SettleAsync(TimeSpan? within = null)
    {
        var until = _simulation.Elapsed + (within ?? TimeSpan.FromMinutes(1));
        while (true)
        {
            var up = Enumerable.Range(0, _nodes.Count).Where(IsUp).Select(i => _nodes[i]).ToList();
            var expected = up.Select(n => n.Address).Order(StringComparer.Ordinal).ToList();
            if (up.All(n => n.Cluster.Members.SequenceEqual(expected)))
                return true;
            if (_simulation.Elapsed >= until)
                return false;
            await _simulation.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private async Task<ActorSystem> StartNodeAsync(string? address)
    {
        var node = await ActorSystem.StartAsync(Network.Transport(address));
        _members.Add(node.Address);
        _simulation.Log($"{node.Address}: started");
        await Cluster.JoinAsync(node, _members, "simulated", _options);
        _setup(node);
        return node;
    }

    /// <summary>
    /// Starts faults the run chooses (rather than the test): every so often (<see cref="ChaosOptions"/>), a node crashes
    /// or comes back, or one is cut off from the others, or the network heals, until <see cref="CalmAsync"/>. Each is a
    /// choice of the run, whose calmest is that nothing happens, so shrinking a failure keeps only the faults it needs.
    /// </summary>
    public void StartChaos(ChaosOptions? options = null)
    {
        if (_chaotic)
            return;
        _chaotic = true;
        _chaos = ChaosAsync(options ?? ChaosOptions.Default);
    }

    /// <summary>
    /// Stops the faults <see cref="StartChaos"/> started, joins the network up, restarts every node that's down, and
    /// waits for them to agree on the members: whether they did (within a minute of simulated time).
    /// </summary>
    public async Task<bool> CalmAsync()
    {
        _chaotic = false;
        await _chaos;
        Heal();
        foreach (var node in _down.Order().ToList())
            await RestartAsync(node);
        return await SettleAsync();
    }

    private async Task ChaosAsync(ChaosOptions options)
    {
        while (true)
        {
            await _simulation.Delay(_simulation.Between(options.MinPause, options.MaxPause));
            if (!_chaotic)
                return;
            var up = Enumerable.Range(0, _nodes.Count).Where(IsUp).ToList();
            var down = _down.Order().ToList();
            // 0 to 3 are calm, so a choice lowered while shrinking takes a fault away.
            switch (_simulation.Random.Next(8))
            {
                case 4 when options.Crashes && up.Count > 1:
                    await CrashAsync(up[_simulation.Random.Next(up.Count)]);
                    break;
                case 5 when down.Count > 0:
                    await RestartAsync(down[_simulation.Random.Next(down.Count)]);
                    break;
                case 6 when options.Splits && up.Count > 1:
                    Isolate(up[_simulation.Random.Next(up.Count)]);
                    break;
                case 7 when options.Splits:
                    Heal();
                    break;
            }
        }
    }

    private async Task StopAllAsync()
    {
        _chaotic = false;
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (_down.Add(i))
                await _nodes[i].AbortAsync();
        }
    }

    /// <summary>The nodes started and not stopped (a crashed node stays listed, as a lease that hasn't run out would).</summary>
    private sealed class Members : IClusterMembership
    {
        private readonly List<string> _addresses = [];

        public void Add(string address)
        {
            if (!_addresses.Contains(address))
                _addresses.Add(address);
        }

        public void Remove(string address) => _addresses.Remove(address);

        public Task JoinAsync(string cluster, string address) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> MembersAsync(string cluster) => Task.FromResult<IReadOnlyList<string>>([.. _addresses]);

        public Task LeaveAsync(string cluster, string address) => Task.CompletedTask;
    }
}

/// <summary>
/// How often a simulated cluster's own faults (<see cref="SimulatedCluster.StartChaos"/>) may come, between pauses of
/// <paramref name="MinPause"/> to <paramref name="MaxPause"/>, and which kinds: nodes crashing (and coming back), and
/// nodes cut off (and the network healing).
/// </summary>
public sealed record ChaosOptions(TimeSpan MinPause, TimeSpan MaxPause, bool Crashes, bool Splits)
{
    /// <summary>Crashes and splits, something chosen every half a second to five seconds.</summary>
    public static readonly ChaosOptions Default = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), true, true);
}
