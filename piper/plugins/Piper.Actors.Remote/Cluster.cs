using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Piper.Runtime;

namespace Piper.Actors.Remote;

/// <summary>Clusters whose nodes are in this process: for tests, and running a cluster's code on one machine.</summary>
public sealed class MemoryClusterMembership : IClusterMembership
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Clusters = new();

    public Task JoinAsync(string cluster, string address)
    {
        Clusters.GetOrAdd(cluster, _ => new())[address] = 0;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> MembersAsync(string cluster) =>
        Task.FromResult<IReadOnlyList<string>>(Clusters.TryGetValue(cluster, out var members) ? [.. members.Keys] : []);

    public Task LeaveAsync(string cluster, string address)
    {
        if (Clusters.TryGetValue(cluster, out var members))
            members.TryRemove(address, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A cluster whose nodes start from a list of seed nodes' addresses (host:port): a StatefulSet's pods' DNS names, say.
/// Nodes not on the list join by pinging those on it, and are learned from them, so the list needs only some members.
/// </summary>
public sealed class StaticClusterMembership(IEnumerable<string> seeds) : IClusterMembership
{
    private readonly IReadOnlyList<string> _seeds = [.. seeds.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct()];

    public Task JoinAsync(string cluster, string address) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> MembersAsync(string cluster) => Task.FromResult(_seeds);

    public Task LeaveAsync(string cluster, string address) => Task.CompletedTask;
}

/// <summary>How often a cluster's nodes ping each other, and how long one can go unheard before it's taken to be down.</summary>
public sealed record ClusterOptions(TimeSpan Heartbeat, TimeSpan DownAfter)
{
    /// <summary>A ping a second; down after five without an answer.</summary>
    public static readonly ClusterOptions Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
}

/// <summary>
/// What a cluster's nodes say to each other: a ping (answered with the nodes the receiver knows) or that one is leaving.
/// A ping also says how many times its sender has taken the receiver to be down (<paramref name="Downs"/>), and whether
/// it takes it to be up now (<paramref name="Up"/>), so the receiver knows when its actors may have run elsewhere.
/// Or it asks the receiver to let go of a singleton (<paramref name="Actor"/>) its sender now owns; a node of an older
/// Piper, which has no singletons, takes that as a ping.
/// </summary>
public sealed record ClusterMessage(string Kind, string From, string[] Known, int Downs = 0, bool Up = false, string? Actor = null);

/// <summary>
/// A cluster: nodes that share out actors among themselves, each actor (a kind and a key, <c>Product/tea</c>) running on
/// one of them, its owner, and moving to another when the members change. A node joins with a membership provider,
/// then hosts kinds of actor (<see cref="Host"/>); a message to an actor in the cluster
/// (<see cref="ActorRef.InCluster"/>) goes to its owner, which starts it as the message arrives.
/// <list type="bullet">
/// <item><b>Members:</b> the nodes ping each other (an actor on each, <c>piper-cluster</c>, over the node's transport),
/// every <see cref="ClusterOptions.Heartbeat"/>; one unheard from for <see cref="ClusterOptions.DownAfter"/> is down.
/// Each node knows the nodes the membership lists, and those the others know.</item>
/// <item><b>Owners:</b> rendezvous hashing over the members that are up, so every node that sees the same members picks
/// the same owner, and a member joining or leaving moves only the actors it gains or loses.</item>
/// <item><b>Moving:</b> when the members change, each node stops the actors it no longer owns (each finishing its
/// message, a persisted one saving its events); messages that reach a node that isn't the owner are dead letters, which
/// asks to the cluster retry on the owner. A persisted actor starting on its new owner replays its events, so it carries
/// on where it was if the nodes share their event store.</item>
/// <item><b>Singletons</b> (<see cref="HostSingleton"/>): one actor of a kind in the whole cluster, started on its owner
/// without waiting for a message. The owner starts it only once every other member that's up has answered that it isn't
/// running it, so a hand-over doesn't overlap.</item>
/// <item><b>Not exact:</b> while nodes disagree on the members (a split network, or the moment a node joins) two may
/// each start an actor. A persisted actor's store refuses the second writer (<c>EventStreamConflictException</c>), and
/// the plugin's code stops an actor that's refused, so it starts again from what's saved. And a node that another took
/// to be down, so may have run its actors meanwhile, restarts its persisted ones once that node sees it again, so they
/// replay what was saved: one still running from before would otherwise answer reads from what it had.</item>
/// </list>
/// </summary>
public sealed class Cluster
{
    /// <summary>The actor on each node that the others ping.</summary>
    public const string ActorName = "piper-cluster";

    private const string Ping = "ping", Leaving = "leaving", Release = "release";

    /// <summary>The answer to a release by a node still running the singleton. (A name no node's address can have.)</summary>
    private const string Held = "/held";

    /// <summary>A singleton's key: it's <c>kind/singleton</c>.</summary>
    internal const string SingletonKey = "singleton";

    private readonly ActorSystem _node;
    private readonly IClusterMembership _membership;
    private readonly ClusterOptions _options;
    private readonly ConcurrentDictionary<string, Action<ActorSystem, string, string>> _kinds = new();
    private readonly ConcurrentDictionary<string, long> _known = new();     // node -> when it was last heard from (0: never)
    private readonly ConcurrentDictionary<string, long> _left = new();      // node -> when it said it was leaving
    private readonly ConcurrentDictionary<string, int> _downs = new();      // node -> how many times this one took it to be down
    private readonly ConcurrentDictionary<string, int> _reported = new();   // node -> how many times it said it took this one to be down
    private readonly ConcurrentDictionary<string, bool> _suspect = new();    // node -> it took this one to be down, and doesn't see it yet
    private readonly ConcurrentDictionary<string, bool> _persisted = new();
    private readonly ConcurrentDictionary<string, byte> _singletons = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _activating = new();
    private readonly Lock _recomputing = new();
    private string[] _members;
    private Task _heartbeat = Task.CompletedTask;
    private volatile bool _leaving;

    private Cluster(ActorSystem node, string name, IClusterMembership membership, ClusterOptions options)
    {
        _node = node;
        Name = name;
        _membership = membership;
        _options = options;
        _members = [node.Address];
    }

    /// <summary>
    /// Joins <paramref name="node"/> to the cluster called <paramref name="name"/>, whose members
    /// <paramref name="membership"/> lists: it pings those it can reach once before answering, so it starts knowing them.
    /// </summary>
    public static async Task<Cluster> JoinAsync(ActorSystem node, IClusterMembership membership, string name = "piper", ClusterOptions? options = null)
    {
        if (node.Joined is { } joined)
            throw new InvalidOperationException($"The node at {node.Address} has already joined the cluster '{joined.Name}'.");
        var cluster = new Cluster(node, name, membership, options ?? ClusterOptions.Default);
        node.Spawn<ClusterMessage>(ActorName, cluster.ReceiveAsync);
        node.Joined = cluster;
        await membership.JoinAsync(name, node.Address);
        await cluster.RoundAsync();
        cluster._heartbeat = node.Runtime.Start(cluster.HeartbeatAsync);
        return cluster;
    }

    /// <summary>
    /// The membership providers a program can choose by configuration (<c>Cluster:Provider</c>): <c>memory</c>, the
    /// local one, and <c>static</c>, which takes <c>Members</c>, the seed nodes' addresses separated by commas.
    /// </summary>
    public static void RegisterProviders()
    {
        PiperProviders.Register<IClusterMembership>("memory", _ => new MemoryClusterMembership(), local: true);
        PiperProviders.Register<IClusterMembership>("static", settings => new StaticClusterMembership(settings.Required("Members").Split(',')));
    }

    public string Name { get; }

    /// <summary>This node.</summary>
    public ActorSystem Node => _node;

    /// <summary>The members that are up, as this node sees them, this one included, in address order.</summary>
    public IReadOnlyList<string> Members => Volatile.Read(ref _members);

    /// <summary>Every node this one knows of, up or down (until it's forgotten), this one included, in address order.</summary>
    public IReadOnlyList<string> Known => [.. _known.Keys.Append(_node.Address).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>Called with the members that are up whenever they change, as this node sees them.</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    /// <summary>
    /// Makes this node host actors of <paramref name="kind"/>: when a message for one reaches this node, its owner, and
    /// it isn't running, <paramref name="activate"/> starts it, given the node, the name to spawn it under, and its key.
    /// </summary>
    /// <param name="persisted">
    /// Its actors keep their state as events in a shared store, so one can be restarted to replay them: as this node
    /// does when another took it to be down, and may have run them meanwhile.
    /// </param>
    public void Host(string kind, Action<ActorSystem, string, string> activate, bool persisted = false)
    {
        if (kind.Contains('/'))
            throw new ArgumentException($"A kind of actor can't contain '/': '{kind}'.", nameof(kind));
        if (_singletons.ContainsKey(kind))
            throw new InvalidOperationException($"{kind} is already hosted as a singleton, so it can't be hosted by key too.");
        _kinds[kind] = activate;
        _persisted[kind] = persisted;
    }

    /// <summary>
    /// Makes this node able to run the cluster's one actor of <paramref name="kind"/> (a scheduler, a coordinator), which
    /// starts on its owner without waiting for a message, moves as members change, and is started again, by the next
    /// heartbeat, if it stops. Every node of the cluster should host it, as any may own it.
    /// </summary>
    public void HostSingleton(string kind, Action<ActorSystem, string> activate, bool persisted = false)
    {
        if (_kinds.ContainsKey(kind))
            throw new InvalidOperationException($"{kind} is already hosted on this node.");
        Host(kind, (node, name, _) => activate(node, name), persisted);
        _singletons[kind] = 0;
        _ = _node.Runtime.Start(() => StartSingletonAsync(kind));
    }

    /// <summary>Starts each singleton this node owns that isn't running.</summary>
    private Task StartSingletonsAsync() =>
        Task.WhenAll(_singletons.Keys.Order(StringComparer.Ordinal).Select(StartSingletonAsync));

    /// <summary>
    /// Starts the singleton of <paramref name="kind"/>, if this node owns it and no other member that's up is running it:
    /// each is asked, and lets go of it if it agrees this node owns it. One that's still finishing a message, or doesn't
    /// answer, puts it off until the next heartbeat (or until that member is found down).
    /// </summary>
    private async Task StartSingletonAsync(string kind)
    {
        var name = $"{kind}/{SingletonKey}";
        if (_leaving || _node.IsStopped || OwnerOf(name) != _node.Address || _node.Actors.Contains(name))
            return;
        var asked = Members.Where(m => m != _node.Address).ToArray();
        var free = await Task.WhenAll(asked.Select(member => ReleasedAsync(member, name)));
        // A member that came up meanwhile hasn't been asked: the next heartbeat does.
        if (free.All(f => f) && Members.All(m => m == _node.Address || asked.Contains(m)))
            Activate(name);
    }

    private async Task<bool> ReleasedAsync(string member, string name)
    {
        try
        {
            var answer = await _node.RequestAsync<ClusterMessage, string[]>(new ActorRef(member, ActorName),
                new ClusterMessage(Release, _node.Address, [], _downs.GetValueOrDefault(member), Members.Contains(member), name), _options.Heartbeat);
            return answer is not [Held];
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The node that owns the actor called <paramref name="name"/> (<c>kind/key</c>), as this node sees the members.</summary>
    public string OwnerOf(string name)
    {
        var members = Volatile.Read(ref _members);
        var best = members[0];
        ulong bestScore = 0;
        foreach (var member in members)
        {
            var score = Score(name, member);
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(member, best) < 0))
                (best, bestScore) = (member, score);
        }
        return best;
    }

    /// <summary>The keys of the actors of <paramref name="kind"/> running on this node.</summary>
    public IReadOnlyList<string> Hosted(string kind) =>
        [.. _node.Actors.Where(a => a.StartsWith(kind + "/", StringComparison.Ordinal)).Select(a => a[(kind.Length + 1)..])];

    /// <summary>
    /// Leaves the cluster: this node stops its actors (each finishing its message, saving what it keeps), then tells the
    /// others, so their messages go to the actors' new owners, and stops pinging. Stopping the node does this first.
    /// </summary>
    public async Task LeaveAsync()
    {
        lock (_activating)
        {
            if (_leaving)
                return;
            _leaving = true;
        }
        await Task.WhenAll(HostedHere().Select(name => _node.StopAsync(new ActorRef(_node.Address, name))));
        // Those moved elsewhere a moment ago too, so none is still running here once the others hear this node has left.
        await _node.FinishAsync();
        foreach (var member in Members.Where(m => m != _node.Address))
            _node.Send(new ActorRef(member, ActorName), new ClusterMessage(Leaving, _node.Address, []));
        // Heartbeats first, so a round under way can't renew this node's membership after it's been removed.
        await StopHeartbeatAsync();
        try
        {
            await _membership.LeaveAsync(Name, _node.Address);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Piper.Actors: the cluster's membership couldn't record {_node.Address} leaving: {error.Message}");
        }
    }

    internal async Task StopHeartbeatAsync()
    {
        _stop.Cancel();
        try
        {
            await _heartbeat;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Starts the actor called <paramref name="name"/> on this node, if it's of a kind this node hosts and this node owns
    /// it: whether it's running now.
    /// </summary>
    internal bool TryActivate(string name)
    {
        // A singleton starts once the others have let go of it, not as a message arrives (an ask is retried meanwhile).
        // And only the one is ever started, whatever key a message names.
        var slash = name.IndexOf('/');
        return slash > 0 && !_singletons.ContainsKey(name[..slash]) && Activate(name);
    }

    private bool Activate(string name)
    {
        var slash = name.IndexOf('/');
        if (_node.IsStopped || slash <= 0 || !_kinds.TryGetValue(name[..slash], out var activate) || OwnerOf(name) != _node.Address)
            return false;
        lock (_activating)
        {
            if (_leaving)
                return false;
            if (_node.Actors.Contains(name))
                return true;
            if (_singletons.ContainsKey(name[..slash]) && _node.IsFinishing(name))
                return false;
            try
            {
                activate(_node, name, name[(slash + 1)..]);
                return true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Piper.Actors: {name} couldn't be started on {_node.Address}: {error.Message}");
                return false;
            }
        }
    }

    private Task ReceiveAsync(ActorContext context)
    {
        if (context.Message is not ClusterMessage message || _leaving)
            return Task.CompletedTask;
        if (message.Kind == Leaving)
        {
            _left[message.From] = _node.Runtime.Time.GetTimestamp();
            Recompute();
            return Task.CompletedTask;
        }
        Heard(message.From);
        Learn(message.Known);
        Reported(message);
        // A ping says its sender is up: a node joining is a member from its first ping, not the next round.
        Recompute();
        // A singleton another node owns, as this node sees it too, was stopped as the members changed; but if one started
        // just then, it's stopped now.
        if (message is { Kind: Release, Actor: { } singleton })
        {
            if (OwnerOf(singleton) != _node.Address)
                _node.StopLocal(singleton);
            context.Respond<string[]>(_node.Actors.Contains(singleton) || _node.IsFinishing(singleton) ? [Held] : []);
            return Task.CompletedTask;
        }
        context.Respond<string[]>([.. Known]);
        return Task.CompletedTask;
    }

    private async Task HeartbeatAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            await _node.Runtime.Delay(_options.Heartbeat, _stop.Token);
            await RoundAsync();
        }
    }

    /// <summary>
    /// Renews this node's membership (a lease, for providers that keep them), asks who the members may be, pings each,
    /// and works out who's up.
    /// </summary>
    private async Task RoundAsync()
    {
        try
        {
            if (!_leaving)
                await _membership.JoinAsync(Name, _node.Address);
            Learn(await _membership.MembersAsync(Name));
        }
        catch (Exception error)
        {
            // The nodes already known carry on; the membership is asked again next time.
            Console.Error.WriteLine($"Piper.Actors: the cluster's membership couldn't be read: {error.Message}");
        }
        var known = _known.Keys.Order(StringComparer.Ordinal).ToArray();
        await Task.WhenAll(known.Select(PingAsync));
        Forget();
        Recompute();
        await StartSingletonsAsync();
    }

    private async Task PingAsync(string member)
    {
        try
        {
            var theirs = await _node.RequestAsync<ClusterMessage, string[]>(
                new ActorRef(member, ActorName), new ClusterMessage(Ping, _node.Address, [.. _known.Keys.Order(StringComparer.Ordinal)],
                    _downs.GetValueOrDefault(member), Members.Contains(member)), _options.Heartbeat);
            Heard(member);
            Learn(theirs);
        }
        catch (Exception)
        {
            // Not heard from this time: down, once it's been long enough.
        }
    }

    private void Heard(string member)
    {
        if (member == _node.Address)
            return;
        _known[member] = _node.Runtime.Time.GetTimestamp();
        _left.TryRemove(member, out _);
    }

    private void Learn(IEnumerable<string> members)
    {
        foreach (var member in members.Where(m => m != _node.Address && m.Length > 0))
            _known.TryAdd(member, 0);
    }

    /// <summary>Forgets nodes not heard from in a long while (pods long gone, with addresses that won't come back).</summary>
    private void Forget()
    {
        var limit = _options.DownAfter * 10;
        foreach (var (member, heard) in _known)
        {
            if (heard != 0 && _node.Runtime.Time.GetElapsedTime(heard) > limit)
                _known.TryRemove(member, out _);
        }
    }

    private bool Up(string member, long heard) =>
        heard != 0 && _node.Runtime.Time.GetElapsedTime(heard) < _options.DownAfter && !(_left.TryGetValue(member, out var left) && left >= heard);

    /// <summary>Works out the members that are up; if they've changed, says so, and stops the actors this node no longer owns.</summary>
    private void Recompute()
    {
        lock (_recomputing)
            RecomputeLocked();
    }

    private void RecomputeLocked()
    {
        string[] members = [_node.Address, .. _known.Where(k => Up(k.Key, k.Value)).Select(k => k.Key)];
        Array.Sort(members, StringComparer.Ordinal);
        var before = Volatile.Read(ref _members);
        if (before.SequenceEqual(members))
            return;
        Volatile.Write(ref _members, members);
        // Asks waiting on a node now down won't be answered: they fail now, rather than at their timeout.
        if (before.Except(members).ToHashSet() is { Count: > 0 } down)
        {
            _node.FailAsksTo(down);
            foreach (var member in down)
                _downs.AddOrUpdate(member, 1, (_, count) => count + 1);
        }
        foreach (var name in HostedHere().Where(name => OwnerOf(name) != _node.Address))
            _ = _node.StopAsync(new ActorRef(_node.Address, name));
        // A node back from being down (to this one) may have taken this one to be down too, and run its actors: they
        // replay what's saved. (A node joining for the first time hasn't; nothing restarts for it.)
        if (members.Except(before).Any(member => _downs.GetValueOrDefault(member) > 0))
            RestartPersisted();
        try
        {
            Changed?.Invoke(members);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Piper.Actors: a handler of the cluster's changes threw: {error.Message}");
        }
    }

    /// <summary>
    /// What a ping says of this node: if its sender has taken this node to be down since it last said, it may have run
    /// this node's actors meanwhile, writing to their streams. Once it sees this node again (so it has stopped its own
    /// copies), this node restarts its persisted actors, which replay those streams. This catches a network lost one
    /// way only, where this node never took the sender to be down, so <see cref="RecomputeLocked"/> doesn't.
    /// </summary>
    private void Reported(ClusterMessage message)
    {
        var before = _reported.GetValueOrDefault(message.From);
        _reported[message.From] = message.Downs;
        if (message.Downs > before)
            _suspect[message.From] = true;
        if (message.Up && _suspect.TryRemove(message.From, out _))
            RestartPersisted();
    }

    /// <summary>
    /// Restarts the persisted actors this node owns: each finishes its message and stops, and starts again at once,
    /// replaying its events from the store, so it has what another node's copy of it saved meanwhile.
    /// </summary>
    private void RestartPersisted()
    {
        foreach (var name in HostedHere().Where(name => _persisted.GetValueOrDefault(name[..name.IndexOf('/')]) && OwnerOf(name) == _node.Address))
            _ = RestartAsync(name);

        async Task RestartAsync(string name)
        {
            try
            {
                await _node.StopAsync(new ActorRef(_node.Address, name));
                if (_singletons.ContainsKey(name[..name.IndexOf('/')]))
                    await StartSingletonAsync(name[..name.IndexOf('/')]);
                else
                    TryActivate(name);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Piper.Actors: {name} couldn't be restarted on {_node.Address}: {error.Message}");
            }
        }
    }

    /// <summary>The actors running on this node of the kinds it hosts.</summary>
    private IEnumerable<string> HostedHere() =>
        _node.Actors.Where(name => name.IndexOf('/') is > 0 and var slash && _kinds.ContainsKey(name[..slash]));

    /// <summary>The rendezvous score of <paramref name="member"/> for <paramref name="name"/>: FNV-1a over both, mixed, so it's the same in every process.</summary>
    private static ulong Score(string name, string member)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(name + "\n" + member))
            hash = (hash ^ b) * 1099511628211UL;
        // FNV's low bits mix poorly for similar inputs; SplitMix64's finaliser spreads them.
        hash ^= hash >> 30;
        hash *= 0xbf58476d1ce4e5b9UL;
        hash ^= hash >> 27;
        hash *= 0x94d049bb133111ebUL;
        return hash ^ (hash >> 31);
    }
}
